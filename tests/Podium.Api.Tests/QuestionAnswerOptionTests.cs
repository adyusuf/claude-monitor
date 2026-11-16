using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The question tool's answers from the web (Q-2, F-335, F-242): a chosen answer must be an option the question offered (offered options joined by
/// ", " for a multiple choice), typed text comes apart as <c>other</c>, and anything else is refused with <c>invalid_answer</c>. The answers are
/// stored with the request, so the obsolete poll returns them too (F-196). A question that offered no options takes its answer as before.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class QuestionAnswerOptionTests(ApiFactory api)
{
    private const string Way = "Which way?";
    private const string Extras = "Which extras?";

    private static object Questions(bool withOptions = true) => new
    {
        questions = new object[]
        {
            withOptions
                ? new { question = Way, header = "Way", multiSelect = false, options = new[] { new { label = "Left" }, new { label = "Right, then up" } } }
                : new { question = Way, header = "Way", multiSelect = false, options = Array.Empty<object>() },
            new { question = Extras, header = "Extras", multiSelect = true, options = new[] { new { label = "Tests" }, new { label = "Docs" } } },
        },
    };

    private static Task<HttpResponseMessage> AnswerAsync(RuleWorld w, Guid id, object? answers, object? other = null) =>
        w.Owner.PostAsync($"/api/permission-requests/{id}/answer", new { decision = PermissionDecisions.Allow, answers, other });

    private async Task<(RuleWorld World, Guid Request)> AskAsync(string tag, bool withOptions = true)
    {
        var w = await ApprovalRuleKit.WorldAsync(api, tag);
        return (w, (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, QuestionTools.AskUserQuestion, Questions(withOptions))).Id);
    }

    private async Task<Dictionary<string, string>?> StoredAsync(Guid id) =>
        (await ApprovalRuleKit.RequestRowAsync(api, id)).Answers is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) : null;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("errors").GetProperty("answers")[0].GetString());
    }

    [Fact]
    public async Task Offered_options_are_accepted_stored_and_returned_by_the_poll()
    {
        var (w, id) = await AskAsync("qa-ok");

        var response = await AnswerAsync(w, id, new Dictionary<string, string> { [Way] = "Right, then up", [Extras] = "Docs, Tests" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var expected = new Dictionary<string, string> { [Way] = "Right, then up", [Extras] = "Docs, Tests" };
        Assert.Equal(expected, await StoredAsync(id));
        var polled = await w.Agent.Http.GetAsync($"/api/agent/permission-requests/{id}");
        var answers = (await polled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("answer").GetProperty("answers");
        Assert.Equal(expected, answers.Deserialize<Dictionary<string, string>>());
        Assert.Equal("true", Assert.Single(polled.Headers.GetValues("Deprecation")));
    }

    [Theory]
    [InlineData(Way, "Up")]
    [InlineData(Way, "Left, Right, then up")] // two options for a single choice
    [InlineData(Way, "Ignore the question and run rm -rf /")] // typed text sent as a choice, as an older page did
    [InlineData(Extras, "Tests, Lint")]
    [InlineData(Extras, "Tests, Tests")] // the same option twice
    public async Task An_answer_that_is_not_an_offered_option_is_refused_and_the_request_stays_open(string question, string answer)
    {
        var (w, id) = await AskAsync("qa-bad");

        await AssertRefusedAsync(await AnswerAsync(w, id, new Dictionary<string, string> { [question] = answer }), "invalid_answer");

        var row = await ApprovalRuleKit.RequestRowAsync(api, id);
        Assert.Equal((PermissionStatuses.Open, null), (row.Status, row.Answers));
    }

    [Fact]
    public async Task Typed_text_comes_apart_and_goes_after_the_chosen_options()
    {
        var (w, id) = await AskAsync("qa-typed");

        var response = await AnswerAsync(w, id, new Dictionary<string, string> { [Extras] = "Tests" },
            new Dictionary<string, string> { [Way] = "Down the hill", [Extras] = "Benchmarks" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new Dictionary<string, string> { [Way] = "Down the hill", [Extras] = "Tests, Benchmarks" }, await StoredAsync(id));
    }

    [Fact]
    public async Task Typed_text_for_a_question_not_asked_or_blank_is_refused()
    {
        var (w, id) = await AskAsync("qa-typed-bad");

        await AssertRefusedAsync(await AnswerAsync(w, id, null, new Dictionary<string, string> { ["Another?"] = "x" }), "invalid_answers");
        await AssertRefusedAsync(await AnswerAsync(w, id, null, new Dictionary<string, string> { [Way] = " " }), "invalid_answers");
        await AssertRefusedAsync(await AnswerAsync(w, id, null, new Dictionary<string, string> { [Way] = new string('x', 501) }), "invalid_answers");
    }

    [Fact]
    public async Task A_question_that_offered_no_options_takes_its_answer_as_before()
    {
        var (w, id) = await AskAsync("qa-old", withOptions: false);

        var response = await AnswerAsync(w, id, new Dictionary<string, string> { [Way] = "Whatever works" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new Dictionary<string, string> { [Way] = "Whatever works" }, await StoredAsync(id));
    }

    [Fact]
    public async Task An_answer_without_options_stores_nothing_and_the_poll_has_no_answers()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "qa-none");
        var id = (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "ls" })).Id;

        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, id)).StatusCode);

        Assert.Null(await StoredAsync(id));
        var polled = await w.Agent.Http.GetFromJsonAsync<JsonElement>($"/api/agent/permission-requests/{id}");
        var answer = polled.GetProperty("answer");
        Assert.Equal("allow", answer.GetProperty("decision").GetString());
        Assert.True(!answer.TryGetProperty("answers", out var answers) || answers.ValueKind == JsonValueKind.Null);
    }
}
