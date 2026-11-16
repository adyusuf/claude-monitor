using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-059 part 2: <c>workspace_invitations.email</c> keeps the address as typed; an invitation from before it keeps its row and gets NULL.</summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationEmailMigrationTests(ApiFactory api)
{
    private const string Before = "20261116065021_EmailKey";
    private const string Migration = "20261116065730_InvitationEmail";

    [Fact]
    public async Task The_column_is_added_nullable_an_old_invitation_keeps_its_row_with_null_and_a_roll_back_drops_it()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var inviter = await m.SeedUserAsync("inviter@mail.com", "inviter@mail.com");
        var connection = await m.SeedConnectionAsync(inviter);
        var invitation = Guid.NewGuid();
        await m.ExecAsync($"""
            INSERT INTO workspace_invitations (id, workspace_id, email_normalized, role, token_hash, invited_by, created_at, expires_at)
            SELECT '{invitation}', workspace_id, 'invitee@mail.com', 'member', 'hash-{invitation:N}', '{inviter}', now(), now() + interval '7 days'
            FROM integration_connections WHERE id = '{connection}'
            """);
        var before = await m.RowsAsync("workspace_invitations");

        await m.MigrateAsync(Migration);

        Assert.Equal("YES", await m.ScalarAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'workspace_invitations' AND column_name = 'email'"));
        Assert.Equal(1, await m.CountAsync($"SELECT count(*) FROM workspace_invitations WHERE id = '{invitation}' AND email IS NULL AND email_normalized = 'invitee@mail.com'"));

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.ColumnCountAsync("workspace_invitations", "email"));
        Assert.Equal(before, await m.RowsAsync("workspace_invitations"));
    }
}
