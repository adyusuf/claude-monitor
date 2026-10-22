using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The fixed agent actions' wire vocabulary (it only grows, global #4) and what the database holds to it (ADR-0005, "Agent actions from the web").</summary>
public sealed class AgentActionContractTests
{
    [Fact]
    public void The_kinds_are_the_four_the_agent_and_the_web_already_speak_in_this_order_then_the_two_for_Claude_then_the_five_remote_writes()
    {
        Assert.Equal(new[] { "restart", "update_check", "update", "status", "claude_start", "claude_stop", "plugin_enable", "plugin_disable", "plugin_update", "plugin_install", "project_setting" }, AgentActionKinds.All.ToArray());
        Assert.Equal(("claude_start", "claude_stop"), (AgentActionKinds.ClaudeStart, AgentActionKinds.ClaudeStop));
        Assert.Equal(("restart", "update_check", "update", "status"),
            (AgentActionKinds.Restart, AgentActionKinds.UpdateCheck, AgentActionKinds.Update, AgentActionKinds.Status));
    }

    [Theory]
    [InlineData(AgentActionKinds.Restart, true)]
    [InlineData(AgentActionKinds.Update, true)]
    [InlineData(AgentActionKinds.UpdateCheck, false)]
    [InlineData(AgentActionKinds.Status, false)]
    [InlineData("", false)]
    [InlineData("RESTART", false)]
    [InlineData("format_disk", false)]
    public void Only_restart_and_update_ask_for_the_re_authentication(string kind, bool expected) =>
        Assert.Equal(expected, AgentActionKinds.NeedsReauth(kind));

    [Fact]
    public void A_queued_action_waits_ten_minutes_and_a_result_is_kept_to_two_thousand_characters()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), AgentActionKinds.QueueTtl);
        Assert.Equal(2000, AgentActionKinds.ResultMax);
    }

    [Fact]
    public void The_statuses_keep_their_wire_values_and_the_agent_may_report_only_taking_and_finishing()
    {
        Assert.Equal(new[] { "queued", "delivered", "succeeded", "failed", "expired" }, AgentActionStatuses.All.ToArray());
        Assert.Equal(new[] { "queued", "delivered" }, AgentActionStatuses.Open.ToArray());
        Assert.Equal(new[] { "delivered", "succeeded", "failed" }, AgentActionStatuses.FromAgent.ToArray());
        Assert.DoesNotContain(AgentActionStatuses.Queued, AgentActionStatuses.FromAgent);
        Assert.DoesNotContain(AgentActionStatuses.Expired, AgentActionStatuses.FromAgent);
    }

    [Fact]
    public void The_error_codes_the_web_reads_keep_their_wire_values()
    {
        Assert.Equal(("invalid_kind", "owner_only"), (AgentActionErrors.InvalidKind, AgentActionErrors.OwnerOnly));
    }

    [Fact]
    public void The_new_error_codes_of_a_remote_start_keep_their_wire_values()
    {
        Assert.Equal(("invalid_first_prompt", "claude_folder_not_trusted", "claude_start_pending"),
            (AgentActionErrors.InvalidFirstPrompt, AgentActionErrors.FolderNotTrusted, AgentActionErrors.ClaudeStartPending));
    }

    [Theory]
    [InlineData("fix the test", true)]
    [InlineData("line one\nline two\ttabbed\r\n", true)]
    [InlineData("", false)]
    [InlineData("   \n\t ", false)]
    [InlineData("bad\u0000text", false)]
    [InlineData("escape \u001b[31m", false)]
    [InlineData(null, false)]
    public void A_first_prompt_is_text_with_letters_in_it_and_no_control_characters_but_new_lines_and_tabs(string? text, bool expected) =>
        Assert.Equal(expected, AgentActionKinds.IsFirstPrompt(text));

    [Fact]
    public void A_first_prompt_is_at_most_2000_characters()
    {
        Assert.True(AgentActionKinds.IsFirstPrompt(new string('x', 2000)));
        Assert.False(AgentActionKinds.IsFirstPrompt(new string('x', 2001)));
        Assert.Equal(2000, AgentActionKinds.ClaudeFirstPromptMax);
    }

    [Fact]
    public void The_automatic_modes_are_off_on_message_and_always_and_their_error_codes_keep_their_wire_values()
    {
        Assert.Equal(["off", "on_message", "always"], ClaudeAutoModes.All);
        Assert.Equal(("invalid_auto_mode", "invalid_auto_project"), (AgentActionErrors.InvalidAutoMode, AgentActionErrors.InvalidAutoProject));
    }

    [Fact]
    public void The_action_event_is_named_action_on_the_agents_stream()
    {
        Assert.Equal("action", AgentStreamEvents.Action);
    }
}

/// <summary>The table's own rules: the CHECKs on kind and status and the one open action of a kind per machine (migration AgentActions).</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentActionStorageTests(ApiFactory api)
{
    private DateTimeOffset Now => api.Clock.GetUtcNow();

    [Fact]
    public async Task The_database_refuses_a_kind_outside_the_closed_set()
    {
        var team = await RemoteKit.TeamAsync(api);

        await Assert.ThrowsAsync<DbUpdateException>(() => ActionKit.InsertAsync(api, team, "format_disk", AgentActionStatuses.Queued, Now, null));

        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("QUEUED")]
    [InlineData("")]
    public async Task The_database_refuses_a_status_outside_the_known_ones(string status)
    {
        var team = await RemoteKit.TeamAsync(api);

        await Assert.ThrowsAsync<DbUpdateException>(() => ActionKit.InsertAsync(api, team, AgentActionKinds.Status, status, Now, null));

        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Theory]
    [InlineData(AgentActionStatuses.Queued, AgentActionStatuses.Queued)]
    [InlineData(AgentActionStatuses.Queued, AgentActionStatuses.Delivered)]
    [InlineData(AgentActionStatuses.Delivered, AgentActionStatuses.Delivered)]
    public async Task The_database_holds_one_open_action_of_a_kind_per_machine(string first, string second)
    {
        var team = await RemoteKit.TeamAsync(api);
        await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, first, Now, null);

        await Assert.ThrowsAsync<DbUpdateException>(() => ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, second, Now, null));

        Assert.Single(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task Finished_actions_of_a_kind_pile_up_beside_one_open_one_and_another_kind_may_be_open_too()
    {
        var team = await RemoteKit.TeamAsync(api);
        await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, AgentActionStatuses.Succeeded, Now, Now);
        await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, AgentActionStatuses.Failed, Now, Now);
        await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, AgentActionStatuses.Expired, Now, Now);
        await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, AgentActionStatuses.Queued, Now, null);
        await ActionKit.InsertAsync(api, team, AgentActionKinds.Status, AgentActionStatuses.Queued, Now, null);

        Assert.Equal(5, (await ActionKit.RowsAsync(api, team.TargetId)).Count);
    }
}
