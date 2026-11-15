using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The device flow under an approval that outlives its conditions, parallel polls and hostile machine facts.</summary>
[Collection(ApiGroup.Name)]
public sealed class DeviceHardeningTests(ApiFactory api)
{
    private async Task<(DeviceCodeResponse Code, HttpClient Agent)> ApprovedCodeAsync(TestUser approver, Guid workspaceId)
    {
        var agent = api.CreateClient();
        var request = new DeviceCodeRequest("machine-" + Guid.NewGuid().ToString("N"), "desk-9", OsKinds.Linux, "x64", "0.3.0");
        var code = (await (await agent.PostAsJsonAsync("/api/device/code", request, TestUser.Json))
            .Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!;
        (await approver.PostAsync("/api/device/approve", new { userCode = code.UserCode, workspaceId })).EnsureSuccessStatusCode();
        return (code, agent);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<DeviceTokenError>(TestUser.Json))!.Error;

    /// <summary>F-017: the approval is only as good as the membership it was given under; a member removed before the poll gets no agent.</summary>
    [Fact]
    public async Task A_member_removed_between_the_approval_and_the_poll_gets_no_agent()
    {
        var owner = await api.NewClient().SignedUpAsync("dev-owner");
        var member = await api.NewClient().SignedUpAsync("dev-member");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = member.Email, role = "member" })).EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        var (code, agent) = await ApprovedCodeAsync(member, owner.WorkspaceId);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).StatusCode);

        var poll = await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(code.DeviceCode), TestUser.Json);
        Assert.Equal(HttpStatusCode.BadRequest, poll.StatusCode);
        Assert.Equal(DeviceTokenErrors.Denied, await ErrorOf(poll));
        await using var db = api.Db();
        Assert.False(await db.Agents.AnyAsync(a => a.UserId == member.Id));
        Assert.False(await db.Machines.AnyAsync(m => m.Hostname == "desk-9" && m.WorkspaceId == owner.WorkspaceId));
        Assert.Equal(DeviceTokenErrors.Denied, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(code.DeviceCode), TestUser.Json)));
    }

    /// <summary>F-020: two polls on one approved code enrol one agent; the second hears the code is used up, never a 500 or a revoked first agent.</summary>
    [Fact]
    public async Task Parallel_polls_on_an_approved_code_enrol_exactly_one_agent()
    {
        for (var round = 0; round < 4; round++)
        {
            var user = await api.NewClient().SignedUpAsync("dev-race");
            var (code, agent) = await ApprovedCodeAsync(user, user.WorkspaceId);
            var poll = new DeviceTokenRequest(code.DeviceCode);

            var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => agent.PostAsJsonAsync("/api/device/token", poll, TestUser.Json)));

            Assert.Equal(1, answers.Count(a => a.StatusCode == HttpStatusCode.OK));
            foreach (var other in answers.Where(a => a.StatusCode != HttpStatusCode.OK))
            {
                Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
                Assert.Equal(DeviceTokenErrors.Expired, await ErrorOf(other));
            }

            var winner = await answers.Single(a => a.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json);
            await using var db = api.Db();
            var agents = await db.Agents.Where(a => a.UserId == user.Id).ToListAsync();
            var only = Assert.Single(agents);
            Assert.Equal(AgentStatuses.Active, only.Status);
            Assert.Equal(winner!.AgentId, only.Id);
        }
    }
}
