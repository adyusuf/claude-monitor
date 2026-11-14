using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>Approving a device needs at least the member role in the chosen workspace and leaves an audit row (DeviceEndpoints.Approve).</summary>
[Collection(ApiGroup.Name)]
public sealed class DeviceApprovalAuditTests(ApiFactory api)
{
    private async Task<(DeviceCodeResponse Code, string Hostname)> NewCodeAsync()
    {
        var hostname = "approve-" + Guid.NewGuid().ToString("N")[..8];
        var request = new DeviceCodeRequest("machine-" + Guid.NewGuid().ToString("N"), hostname, OsKinds.Windows, "x64", "0.3.0");
        var response = await api.CreateClient().PostAsJsonAsync("/api/device/code", request, TestUser.Json);
        return ((await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!, hostname);
    }

    [Fact]
    public async Task A_viewer_cannot_approve_a_device_and_the_code_stays_pending()
    {
        var owner = await api.NewClient().SignedUpAsync("approve-owner");
        var viewer = await RemoteKit.MemberAsync(api, owner, "approve-viewer", "viewer");
        var (code, _) = await NewCodeAsync();

        var response = await viewer.PostAsync("/api/device/approve", new { userCode = code.UserCode, workspaceId = owner.WorkspaceId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("not_a_member", errors.GetProperty("workspaceId")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync("/api/device/lookup/" + code.UserCode)).StatusCode);
        await using var db = api.Db();
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Action == AuditActions.DeviceApproved && a.ActorUserId == viewer.Id));
    }

    [Fact]
    public async Task Approving_writes_the_device_approved_audit_row_with_the_machine_it_named()
    {
        var owner = await api.NewClient().SignedUpAsync("approve-audit");
        var member = await RemoteKit.MemberAsync(api, owner, "approve-member");
        var (code, hostname) = await NewCodeAsync();

        var response = await member.PostAsync("/api/device/approve", new { userCode = code.UserCode, workspaceId = owner.WorkspaceId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = api.Db();
        var device = await db.DeviceAuthorizations.AsNoTracking().SingleAsync(d => d.RequestedHostname == hostname);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.DeviceApproved && a.ActorUserId == member.Id);
        Assert.Equal((owner.WorkspaceId, "device", device.Id.ToString()), (audit.WorkspaceId, audit.TargetType, audit.TargetId));
        Assert.Equal((hostname, OsKinds.Windows),
            (audit.Detail!.RootElement.GetProperty("RequestedHostname").GetString(), audit.Detail.RootElement.GetProperty("RequestedOs").GetString()));
    }
}
