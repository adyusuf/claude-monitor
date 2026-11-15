using System.Net;
using System.Text;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// Half of a surrogate pair (JSON "\ud800") is as unstorable as a NUL: PostgreSQL text refuses it. The JSON binder already refuses such a string (400, no
/// problem body: the handlers are never reached), so the owner's deny reason, a run's reason and an ask's question can never end in a 500 from the
/// database; these tests pin that, so a change of the binder would show here. A whole pair (an emoji) is still accepted.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ReasonSurrogateTests(ApiFactory api)
{
    private const string Hole = "HOLE-FOR-RAW-TEXT";

    private static StringContent Raw(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostRawAsync(TestUser user, string url, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Raw(json) };
        request.Headers.Add("X-CSRF", "1");
        return await user.Http.SendAsync(request);
    }

    private static string Serialized(object value, string escaped) =>
        JsonSerializer.Serialize(value, TestUser.Json).Replace(Hole, escaped, StringComparison.Ordinal);

    [Theory]
    [InlineData("no\\ud800thanks")]
    [InlineData("no\\udc00thanks")]
    [InlineData("\\ud83d")]
    public async Task A_deny_reason_with_half_a_surrogate_pair_is_refused_and_the_run_stays_waiting(string escaped)
    {
        var team = await RemoteKit.TeamAsync(api);
        var id = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "tests"))).Id;

        var response = await PostRawAsync(team.Owner, $"/api/runs/{id}/deny", $$"""{"reason":"{{escaped}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_deny_reason_with_a_whole_surrogate_pair_is_accepted()
    {
        var team = await RemoteKit.TeamAsync(api);
        var id = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "tests"))).Id;

        var response = await PostRawAsync(team.Owner, $"/api/runs/{id}/deny", """{"reason":"not now 😀"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(RunStatuses.Denied, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_run_reason_with_half_a_surrogate_pair_is_refused()
    {
        var team = await RemoteKit.TeamAsync(api);
        var body = Serialized(RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: Hole), "bad\\ud800reason");

        var response = await team.Requester.Http.PostAsync("/api/agent/runs", Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_ask_question_with_half_a_surrogate_pair_is_refused()
    {
        var team = await AskKit.TeamAsync(api);
        var body = Serialized(AskKit.Ask(team.TargetId, question: Hole), "why\\udc00 so");

        var response = await team.Asker.Http.PostAsync("/api/agent/asks", Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
