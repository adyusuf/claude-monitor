using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Text;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>S-5: two invitations of one person to one workspace accepted at once: the second membership insert meets the first one's key and must not answer 500.</summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationAcceptTwoRaceTests(ApiFactory api)
{
    [Fact]
    public async Task Two_invitations_to_one_workspace_accepted_in_parallel_both_succeed_and_leave_one_membership()
    {
        var owner = await api.NewClient().SignedUpAsync("race2-owner");
        var invitee = await api.NewClient().SignedUpAsync("race2-invitee");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitee.Email, role = "member" })).EnsureSuccessStatusCode();
        var first = api.Mail.TokenFor(invitee.Email);
        // The invite endpoint refuses a second open invitation to the same address, so the second one (an older one, a mirrored one) is written directly.
        var second = Secrets.NewToken();
        await using (var seed = api.Db())
        {
            var now = api.Clock.GetUtcNow();
            seed.WorkspaceInvitations.Add(new WorkspaceInvitation
            {
                WorkspaceId = owner.WorkspaceId, EmailNormalized = SearchText.Email(invitee.Email), Role = Roles.Member,
                TokenHash = Secrets.Hash(second), InvitedBy = owner.Id, CreatedAt = now, ExpiresAt = now.AddDays(7),
            });
            await seed.SaveChangesAsync();
        }

        var meet = new MeetAfterMemberRead();
        await using var host = HostKit.WithInterceptor(api, meet);
        var one = await HostKit.SignInAsync(api, host, invitee);
        var two = await HostKit.SignInAsync(api, host, invitee);
        meet.Armed = true; // from here both accepts have read "no member row" before either writes

        var answers = await Task.WhenAll(one.PostAsync("/api/invitations/accept", new { token = first }), two.PostAsync("/api/invitations/accept", new { token = second }));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK], answers.Select(a => a.StatusCode));
        await using var db = api.Db();
        Assert.Equal(1, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == invitee.Id && m.RemovedAt == null));
        Assert.Equal(2, await db.WorkspaceInvitations.CountAsync(i => i.WorkspaceId == owner.WorkspaceId && i.AcceptedAt != null));
    }

    /// <summary>Holds the first two membership lookups of the accept (the ones by user) after they have read, until both have, so neither writes before both have read.</summary>
    private sealed class MeetAfterMemberRead : DbCommandInterceptor
    {
        private readonly Barrier barrier = new(2);
        private int hits;

        public bool Armed { get; set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM workspace_members", StringComparison.Ordinal)
                && command.CommandText.Contains("user_id", StringComparison.Ordinal) && command.Parameters.Count == 2
                && command.Parameters.Cast<DbParameter>().Any(p => p.ParameterName.Contains("userId", StringComparison.Ordinal))
                && Interlocked.Increment(ref hits) <= 2)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken), "the other accept never read the membership");
            }

            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
