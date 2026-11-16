using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-180, deploy window: an account written by a release that does not know <c>users.email_key</c> (the old one while a deploy migrates, or
/// after a roll-back) still gets its key from the trigger, and the accounts it made before are filled once. A key two accounts would share
/// is refused (23505), never skipped.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class EmailKeyTriggerMigrationTests(ApiFactory api)
{
    private const string Before = "20261116071141_SprintActiveIndexArchived";
    private const string Migration = "20261116072641_EmailKeyTrigger";

    private static Task<object?> KeyAsync(MigrationDb m, Guid user) => m.ScalarAsync($"SELECT email_key FROM users WHERE id = '{user}'");

    [Fact]
    public async Task Rows_written_without_the_key_get_it_old_rows_are_filled_a_shared_key_is_refused_and_a_roll_back_drops_the_trigger()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var early = await m.SeedUserAsync("Early.Old@Mail.com", "early.old@mail.com");
        Assert.Null(await KeyAsync(m, early));

        await m.MigrateAsync(Migration);

        Assert.Equal("early.old@mail.com", await KeyAsync(m, early));
        var late = await m.SeedUserAsync("Late.Old@Mail.com", "late.old@mail.com");
        Assert.Equal("late.old@mail.com", await KeyAsync(m, late));
        await m.ExecAsync($"UPDATE users SET email = 'Moved@Mail.com', email_normalized = 'moved@mail.com' WHERE id = '{late}'");
        Assert.Equal("moved@mail.com", await KeyAsync(m, late));
        await m.ExecAsync($"UPDATE users SET email = NULL, email_normalized = NULL WHERE id = '{late}'");
        Assert.Null(await KeyAsync(m, late));
        await m.ExecAsync($"UPDATE users SET email_key = 'kept@mail.com', email = 'Kept@Mail.com' WHERE id = '{late}'");
        Assert.Equal("kept@mail.com", await KeyAsync(m, late));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => m.SeedUserAsync("EARLY.old@mail.com", "early.old@mail.com-other"));
        Assert.Contains("23505", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ix_users_email_key", refused.Message, StringComparison.Ordinal);

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.CountAsync("SELECT count(*) FROM pg_trigger WHERE tgname = 'users_email_key_fill'"));
        Assert.Equal(0, await m.CountAsync("SELECT count(*) FROM pg_proc WHERE proname = 'users_email_key_fill'"));
        Assert.Null(await KeyAsync(m, await m.SeedUserAsync("after@mail.com", "after@mail.com")));
    }

    [Fact]
    public async Task An_old_row_whose_key_another_account_holds_stops_the_migration_before_it_changes_anything()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var holder = await m.SeedUserAsync("same.box@mail.com", "same.box@mail.com");
        await m.ExecAsync($"UPDATE users SET email_key = 'same.box@mail.com' WHERE id = '{holder}'");
        await m.SeedUserAsync("Same.Box@Mail.com", "same.box@mail.com-other");
        var rows = await m.RowsAsync("users");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => m.MigrateAsync(Migration));

        Assert.Contains("users: 1 e-mail identity keys are shared by more than one account", error.Message, StringComparison.Ordinal);
        Assert.Equal(rows, await m.RowsAsync("users"));
        Assert.Equal(0, await m.CountAsync("SELECT count(*) FROM pg_trigger WHERE tgname = 'users_email_key_fill'"));
        Assert.False(await m.AppliedAsync(Migration));
    }
}
