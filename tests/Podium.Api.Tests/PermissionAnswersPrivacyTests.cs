using System.Net;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The chosen answers kept with a permission request (F-196) are captured content: an account deletion clears them with the tool input.</summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionAnswersPrivacyTests(ApiFactory api)
{
    [Fact]
    public async Task Deleting_the_account_clears_the_chosen_answers_with_the_tool_input()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "pa-delete");
        var questions = new[] { new { question = "Which way?", multiSelect = false, options = new[] { new { label = "Left" }, new { label = "Right" } } } };
        var id = (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, QuestionTools.AskUserQuestion, new { questions })).Id;
        var answered = await w.Owner.PostAsync($"/api/permission-requests/{id}/answer",
            new { decision = PermissionDecisions.Allow, answers = new Dictionary<string, string> { ["Which way?"] = "Left" }, other = new Dictionary<string, string> { ["Which way?"] = "private words" } });
        Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
        Assert.Contains("private words", (await ApprovalRuleKit.RequestRowAsync(api, id)).Answers, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        var row = await ApprovalRuleKit.RequestRowAsync(api, id);
        Assert.Null(row.Answers);
        Assert.Equal("{}", row.ToolInput.RootElement.GetRawText());
    }
}
