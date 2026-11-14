using System.Net;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>How a rule that asks for the same scope is put in place (ADR-0019 §9): one live rule per scope, extended and restarted, never narrowed, and the cap.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleStandingTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private async Task<List<ApprovalRule>> RulesOfAsync(RuleWorld w) => await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId);

    private async Task<int> LiveAsync(RuleWorld w)
    {
        var now = api.Clock.GetUtcNow();
        return (await RulesOfAsync(w)).Count(r => r.RevokedAt is null && (r.ExpiresAt is null || r.ExpiresAt > now));
    }

    [Fact]
    public async Task A_second_answer_for_a_live_scope_extends_the_same_rule_to_the_later_end_and_audits_the_change()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        var seeded = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes, createdAt: now - 2 * Minute, expiresAt: now + 3 * Minute);

        var requestId = await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.FiveMinutes);

        var rule = Assert.Single(await RulesOfAsync(w));
        Assert.Equal((seeded.Id, seeded.CreatedAt, seeded.CreatedFromRequest), (rule.Id, rule.CreatedAt, rule.CreatedFromRequest));
        Assert.Equal((ApprovalDurations.FiveMinutes, now + 5 * Minute), (rule.Duration, rule.ExpiresAt));
        var audit = Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
        Assert.Equal((rule.Id.ToString(), requestId), (audit.TargetId, audit.Detail!.RootElement.GetProperty("request").GetGuid()));
    }

    [Fact]
    public async Task A_second_answer_that_asks_always_makes_the_live_rule_stand_always()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var seeded = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.Always);

        var rule = Assert.Single(await RulesOfAsync(w));
        Assert.Equal((seeded.Id, ApprovalDurations.Always, null), (rule.Id, rule.Duration, rule.ExpiresAt));
    }

    [Theory]
    [InlineData(ApprovalDurations.OneHour, ApprovalDurations.FiveMinutes)]
    [InlineData(ApprovalDurations.Always, ApprovalDurations.OneHour)]
    public async Task A_shorter_answer_never_narrows_a_rule_that_stands_longer_and_writes_no_audit_row(string standing, string asked)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var seeded = await ApprovalRuleKit.SeedAsync(api, w, "make build", standing);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, asked);

        var rule = Assert.Single(await RulesOfAsync(w));
        Assert.Equal((standing, seeded.ExpiresAt), (rule.Duration, rule.ExpiresAt));
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
    }

    [Fact]
    public async Task An_answer_for_a_scope_whose_rule_has_ended_starts_that_rule_again_instead_of_adding_a_second_one()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        var ended = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes, createdAt: now - 6 * Minute, expiresAt: now - Minute);

        var requestId = await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.TenMinutes);

        var rule = Assert.Single(await RulesOfAsync(w));
        var answered = (await ApprovalRuleKit.RequestRowAsync(api, requestId)).AnsweredAt;
        Assert.Equal((ended.Id, answered, requestId), (rule.Id, rule.CreatedAt, rule.CreatedFromRequest));
        Assert.Equal((ApprovalDurations.TenMinutes, answered + 10 * Minute), (rule.Duration, rule.ExpiresAt));
        Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
    }

    [Fact]
    public async Task A_revoked_rule_does_not_stand_in_the_way_of_a_new_one_for_the_same_scope()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        var revoked = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.OneHour, revokedAt: now - Minute);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.FiveMinutes);

        var rules = await RulesOfAsync(w);
        Assert.Equal(2, rules.Count);
        Assert.Equal((revoked.Id, now - Minute), (rules.Single(r => r.RevokedAt is not null).Id, rules.Single(r => r.RevokedAt is not null).RevokedAt));
        Assert.Equal(("5m", null), (rules.Single(r => r.RevokedAt is null).Duration, rules.Single(r => r.RevokedAt is null).LastUsedAt));
    }

    [Fact]
    public async Task The_same_command_in_another_project_or_another_tool_is_another_rule()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var other = await ApprovalRuleKit.StartSessionAsync(api, w.Agent, ApprovalRuleKit.OtherProject);
        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.OneHour);
        var inOther = await ApprovalRuleKit.AskAsync(w.Agent, other, "Bash", Make);
        var otherTool = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", new { file_path = "/repo/build.log" }, accessScope: AccessScopes.Inside);

        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, inOther.Id, remember: ApprovalDurations.OneHour)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, otherTool.Id, remember: ApprovalDurations.OneHour)).StatusCode);

        var rules = await RulesOfAsync(w);
        Assert.Equal(3, rules.Count);
        Assert.Equal(3, rules.Select(r => (r.RepoId, r.ToolName)).Distinct().Count());
    }

    // ---- the cap ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_with_the_cap_of_live_rules_gets_a_conflict_for_a_new_scope_and_the_request_stays_open()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.SeedLiveAsync(api, w, ApprovalRuleLimits.LiveMax);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        var response = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.OneHour);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleLimit);
        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
        Assert.Equal(ApprovalRuleLimits.LiveMax, (await RulesOfAsync(w)).Count);
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
    }

    [Fact]
    public async Task Ended_and_revoked_rules_do_not_count_towards_the_cap_so_the_last_free_place_can_be_taken_and_then_the_next_is_refused()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        await ApprovalRuleKit.SeedLiveAsync(api, w, ApprovalRuleLimits.LiveMax - 1);
        await ApprovalRuleKit.SeedAsync(api, w, "ended", ApprovalDurations.FiveMinutes, createdAt: now - 6 * Minute, expiresAt: now - Minute);
        await ApprovalRuleKit.SeedAsync(api, w, "revoked", revokedAt: now - Minute);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.OneHour);
        var next = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make test" });

        Assert.Equal(ApprovalRuleLimits.LiveMax, await LiveAsync(w));
        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, next.Id, remember: ApprovalDurations.OneHour), HttpStatusCode.Conflict, CommandEndpoints.RuleLimit);
    }

    [Fact]
    public async Task Starting_an_ended_rule_again_counts_as_a_new_place_and_is_refused_at_the_cap_leaving_the_rule_ended()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        await ApprovalRuleKit.SeedLiveAsync(api, w, ApprovalRuleLimits.LiveMax);
        var ended = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes, createdAt: now - 6 * Minute, expiresAt: now - Minute);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        var response = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.FiveMinutes);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleLimit);
        Assert.Equal(ended.ExpiresAt, (await RulesOfAsync(w)).Single(r => r.Id == ended.Id).ExpiresAt);
    }

    [Fact]
    public async Task Extending_a_live_rule_is_allowed_at_the_cap_because_it_takes_no_new_place()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.SeedLiveAsync(api, w, ApprovalRuleLimits.LiveMax - 1);
        var live = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.Always);

        var rule = (await RulesOfAsync(w)).Single(r => r.Id == live.Id);
        Assert.Equal((ApprovalDurations.Always, null), (rule.Duration, rule.ExpiresAt));
        Assert.Equal(ApprovalRuleLimits.LiveMax, (await RulesOfAsync(w)).Count);
    }

    // ---- an always-rule needs re-authentication ----------------------------------------------------------------------

    /// <summary>The sign-in is older than the re-authentication window by the time the request is answered, and the request is still open.</summary>
    private async Task<PermissionRequestCreated> StaleRequestAsync(RuleWorld w)
    {
        api.Clock.Advance(TimeSpan.FromMinutes(5));
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, waitSeconds: 600);
        api.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        return request;
    }

    [Fact]
    public async Task An_always_rule_after_a_fresh_sign_in_is_made_with_no_expiry_and_the_audit_row_names_the_sign_in()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.Always);

        var rule = Assert.Single(await RulesOfAsync(w));
        Assert.Equal((ApprovalDurations.Always, null), (rule.Duration, rule.ExpiresAt));
        var audit = Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
        Assert.Equal(ReauthWays.SignIn, audit.Detail!.RootElement.GetProperty("reauth").GetString());
    }

    [Fact]
    public async Task An_always_rule_after_the_sign_in_went_stale_is_refused_until_the_person_signs_in_again()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await StaleRequestAsync(w);

        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.Always), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);

        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
        Assert.Empty(await RulesOfAsync(w));
        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = w.Owner.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(again, request.Id, remember: ApprovalDurations.Always)).StatusCode);
        Assert.Equal(ApprovalDurations.Always, Assert.Single(await RulesOfAsync(w)).Duration);
    }

    [Fact]
    public async Task A_timed_rule_needs_no_re_authentication_even_after_the_sign_in_went_stale()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await StaleRequestAsync(w);

        var response = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.OneHour);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ApprovalDurations.OneHour, Assert.Single(await RulesOfAsync(w)).Duration);
    }

    [Fact]
    public async Task An_always_rule_with_a_stale_sign_in_takes_a_valid_two_step_code_and_refuses_a_wrong_one()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, w.Owner);
        var request = await StaleRequestAsync(w);

        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.Always, code: "000000"), HttpStatusCode.Forbidden,
            RemoteChecks.ReauthRequired);
        var accepted = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.Always, code: RemoteKit.TotpNow(api, secret));

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(ApprovalDurations.Always, Assert.Single(await RulesOfAsync(w)).Duration);
        var audit = Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
        Assert.Equal(ReauthWays.Code, audit.Detail!.RootElement.GetProperty("reauth").GetString());
    }

    // ---- two answers for one scope --------------------------------------------------------------------------------

    [Fact]
    public async Task When_another_answer_made_the_same_rule_a_moment_earlier_this_answer_stands_and_extends_that_rule()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);
        var rival = ApprovalRuleKit.Build(api, w, await ApprovalRuleKit.RepoIdAsync(api, w.WorkspaceId), "make build", ApprovalDurations.FiveMinutes);
        // The other answer's rule is written after this answer has looked and before it inserts: the database refuses the second live rule of one scope.
        var fault = new CommandFault("INSERT INTO approval_rules") { Before = () => ApprovalRuleKit.SeedNow(api, rival) };
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, w.Owner);

        var response = await ApprovalRuleKit.AnswerAsync(owner, request.Id, remember: ApprovalDurations.Always);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rule = Assert.Single(await RulesOfAsync(w));
        Assert.Equal((rival.Id, ApprovalDurations.Always, null), (rule.Id, rule.Duration, rule.ExpiresAt));
        var row = await ApprovalRuleKit.RequestRowAsync(api, request.Id);
        Assert.Equal((PermissionStatuses.Answered, PermissionDecisions.Allow), (row.Status, row.Decision));
        Assert.Equal(rule.Id.ToString(), Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated)).TargetId);
    }
}
