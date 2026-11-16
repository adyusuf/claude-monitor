using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-180: <c>ix_users_email_normalized</c> becomes a plain search index, so two mailboxes that fold to one value ("josé@" and "jose@") can be
/// two accounts; identity stays unique on <c>ix_users_email_key</c>. The roll-back never deletes or merges an account: it stops, naming the count.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class EmailNormalizedNonUniqueMigrationTests(ApiFactory api)
{
    private const string Before = "20261116073003_PermissionRequestKeyLength";
    private const string Migration = "20261116075448_EmailNormalizedNonUnique";
    private const string Index = "ix_users_email_normalized";

    [Fact]
    public async Task Look_alike_addresses_become_two_accounts_a_shared_key_is_still_refused_and_a_clean_roll_back_restores_uniqueness()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        await m.SeedUserAsync("jose@mail.com", "jose@mail.com");
        Assert.Contains(Index, (await Assert.ThrowsAnyAsync<Exception>(() => m.SeedUserAsync("josé@mail.com", "jose@mail.com"))).Message, StringComparison.Ordinal);

        await m.MigrateAsync(Migration);

        Assert.Equal("CREATE INDEX ix_users_email_normalized ON public.users USING btree (email_normalized)", await m.IndexAsync(Index));
        var accented = await m.SeedUserAsync("josé@mail.com", "jose@mail.com");
        await m.SeedUserAsync("a@gmaıl.com", "a@gmail.com");
        await m.SeedUserAsync("a@gmail.com", "a@gmail.com");
        Assert.Equal(2, await m.CountAsync("SELECT count(*) FROM users WHERE email_normalized = 'jose@mail.com'"));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => m.SeedUserAsync("JOSÉ@mail.com", "jose@mail.com"));
        Assert.Contains("23505", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ix_users_email_key", refused.Message, StringComparison.Ordinal);

        await m.ExecAsync($"DELETE FROM users WHERE id = '{accented}' OR email = 'a@gmaıl.com'");
        await m.MigrateAsync(Before);
        Assert.Equal("CREATE UNIQUE INDEX ix_users_email_normalized ON public.users USING btree (email_normalized)", await m.IndexAsync(Index));
    }

    [Fact]
    public async Task A_roll_back_with_two_accounts_on_one_folded_address_stops_and_changes_nothing()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        await m.MigrateAsync(Migration);
        await m.SeedUserAsync("jose@mail.com", "jose@mail.com");
        await m.SeedUserAsync("josé@mail.com", "jose@mail.com");
        var rows = await m.RowsAsync("users");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => m.MigrateAsync(Before));

        Assert.Contains("users: 1 normalised e-mail addresses are shared by more than one account", error.Message, StringComparison.Ordinal);
        Assert.Equal(rows, await m.RowsAsync("users"));
        Assert.Equal("CREATE INDEX ix_users_email_normalized ON public.users USING btree (email_normalized)", await m.IndexAsync(Index));
        Assert.True(await m.AppliedAsync(Migration));
    }
}
