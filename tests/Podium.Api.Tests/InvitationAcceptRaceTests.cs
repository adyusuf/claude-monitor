using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// Accepting claims the invitation with a compare-and-set (InvitationEndpoints.Accept): a revoke that commits after Accept read the
/// invitation wins, and two accepts of one token at once let exactly one through.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationAcceptRaceTests(ApiFactory api)
{
    private async Task<string> InviteAsync(TestUser owner, TestUser invitee)
    {
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitee.Email, role = "member" })).EnsureSuccessStatusCode();
        return api.Mail.TokenFor(invitee.Email);
    }

    [Fact]
    public async Task A_revoke_that_commits_after_accept_read_the_invitation_wins()
    {
        var owner = await api.NewClient().SignedUpAsync("race-rev-owner");
        var invitee = await api.NewClient().SignedUpAsync("race-rev-invitee");
        var token = await InviteAsync(owner, invitee);
        var invitationId = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations"))[0].GetProperty("id").GetGuid();
        var fault = new CommandFault("FROM workspace_members");
        await using var host = HostKit.WithInterceptor(api, fault);
        var client = await HostKit.SignInAsync(api, host, invitee);
        // Accept has read the invitation and is looking up the membership: the admin's revoke (the same predicate as the endpoint) commits now.
        fault.Before = () =>
        {
            using var db = api.Db();
            Assert.Equal(1, db.WorkspaceInvitations
                .Where(i => i.Id == invitationId && i.AcceptedAt == null && i.RevokedAt == null)
                .ExecuteUpdate(s => s.SetProperty(i => i.RevokedAt, api.Clock.GetUtcNow())));
        };

        var response = await client.PostAsync("/api/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("invalid_token", errors.GetProperty("token")[0].GetString());
        Assert.Null(fault.Before); // the revoke did run inside the accept
        await using var check = api.Db();
        Assert.False(await check.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == invitee.Id));
        var invitation = await check.WorkspaceInvitations.AsNoTracking().SingleAsync(i => i.Id == invitationId);
        Assert.Null(invitation.AcceptedAt);
        Assert.NotNull(invitation.RevokedAt);
        Assert.False(await check.AuditEvents.AnyAsync(a => a.WorkspaceId == owner.WorkspaceId && a.Action == AuditActions.InvitationAccepted));
    }

    [Fact]
    public async Task Two_accepts_of_one_token_at_once_let_exactly_one_through()
    {
        var owner = await api.NewClient().SignedUpAsync("race-two-owner");
        var invitee = await api.NewClient().SignedUpAsync("race-two-invitee");
        var token = await InviteAsync(owner, invitee);
        var meet = new MeetBeforeMembers();
        await using var host = HostKit.WithInterceptor(api, meet);
        var first = await HostKit.SignInAsync(api, host, invitee);
        var second = await HostKit.SignInAsync(api, host, invitee);
        meet.Armed = true; // from here both accepts have read the invitation before either goes on to write

        var answers = await Task.WhenAll(
            first.PostAsync("/api/invitations/accept", new { token }),
            second.PostAsync("/api/invitations/accept", new { token }));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.BadRequest], answers.Select(a => a.StatusCode).Order());
        await using var db = api.Db();
        Assert.Equal(1, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == invitee.Id));
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.WorkspaceId == owner.WorkspaceId && a.Action == AuditActions.InvitationAccepted));
    }

    /// <summary>Holds the first two membership lookups until both have arrived, so two requests have both read the invitation before either writes.</summary>
    private sealed class MeetBeforeMembers : DbCommandInterceptor
    {
        private readonly Barrier barrier = new(2);
        private int hits;

        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM workspace_members", StringComparison.Ordinal) && Interlocked.Increment(ref hits) <= 2)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken), "the second accept never reached the membership lookup");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
