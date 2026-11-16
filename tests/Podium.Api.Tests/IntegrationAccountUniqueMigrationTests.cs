using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-080, F-337: one Atlassian account per connection, made race-proof by the unique index <c>ux_integration_accounts_account</c>. An account
/// already linked by two people stops the migration before it changes anything; it never deletes a link.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationAccountUniqueMigrationTests(ApiFactory api)
{
    private const string Before = "20261116070141_IntegrationPersonUnique";
    private const string Migration = "20261116070419_IntegrationAccountUnique";

    private static Task LinkAsync(MigrationDb m, Guid connection, Guid user, string account) => m.ExecAsync($"""
        INSERT INTO integration_accounts (id, connection_id, user_id, external_account_id, display_name, refresh_token, access_token, access_expires_at, status, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{connection}', '{user}', '{account}', 'Linked', 'sealed-placeholder', 'sealed-placeholder', now(), 'active', now(), now())
        """);

    [Fact]
    public async Task One_account_linked_by_two_people_stops_the_migration_and_no_row_changes()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var first = await m.SeedUserAsync("first@mail.com", "first@mail.com");
        var second = await m.SeedUserAsync("second@mail.com", "second@mail.com");
        var connection = await m.SeedConnectionAsync(first);
        await LinkAsync(m, connection, first, "atl-shared");
        await LinkAsync(m, connection, second, "atl-shared");
        var rows = await m.RowsAsync("integration_accounts");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => m.MigrateAsync(Migration));

        Assert.Contains("integration_accounts: 1 Atlassian accounts are linked by more than one person of a connection", error.Message, StringComparison.Ordinal);
        Assert.Equal(rows, await m.RowsAsync("integration_accounts"));
        Assert.Null(await m.IndexAsync("ux_integration_accounts_account"));
        Assert.False(await m.AppliedAsync(Migration));
    }

    [Fact]
    public async Task Clean_links_get_the_index_and_a_second_person_on_the_same_account_is_refused()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var first = await m.SeedUserAsync("one@mail.com", "one@mail.com");
        var second = await m.SeedUserAsync("two@mail.com", "two@mail.com");
        var connection = await m.SeedConnectionAsync(first);
        var other = await m.SeedConnectionAsync(first);
        // The same account on two connections is allowed: the rule is per connection.
        await LinkAsync(m, connection, first, "atl-1");
        await LinkAsync(m, other, second, "atl-1");
        var rows = await m.RowsAsync("integration_accounts");

        await m.MigrateAsync(Migration);

        Assert.Equal(rows, await m.RowsAsync("integration_accounts"));
        Assert.Equal(
            "CREATE UNIQUE INDEX ux_integration_accounts_account ON public.integration_accounts USING btree (connection_id, external_account_id)",
            await m.IndexAsync("ux_integration_accounts_account"));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => LinkAsync(m, connection, second, "atl-1"));
        Assert.Contains("ux_integration_accounts_account", refused.Message, StringComparison.Ordinal);

        await m.MigrateAsync(Before);
        Assert.Null(await m.IndexAsync("ux_integration_accounts_account"));
    }
}
