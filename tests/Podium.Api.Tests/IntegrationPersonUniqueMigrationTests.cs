using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-068: one person per member per connection, made race-proof by the partial unique index <c>ux_integration_people_user</c>. A member
/// already linked twice stops the migration before it changes anything; it never deletes or relinks a person.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPersonUniqueMigrationTests(ApiFactory api)
{
    private const string Before = "20261116065942_MirrorMissedPasses";
    private const string Migration = "20261116070141_IntegrationPersonUnique";

    private static string Person(Guid connection, string account, Guid? user) =>
        $"('{Guid.NewGuid()}', '{connection}', '{account}', 'Person {account}', {(user is null ? "NULL" : $"'{user}'")}, true, now(), now())";

    private static Task SeedPeopleAsync(MigrationDb m, params string[] rows) => m.ExecAsync(
        "INSERT INTO integration_people (id, connection_id, external_account_id, display_name, user_id, active, first_seen_at, last_seen_at) VALUES "
        + string.Join(", ", rows));

    [Fact]
    public async Task A_member_linked_to_two_persons_stops_the_migration_and_no_row_changes()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var member = await m.SeedUserAsync("member@mail.com", "member@mail.com");
        var connection = await m.SeedConnectionAsync(member);
        await SeedPeopleAsync(m, Person(connection, "acc-1", member), Person(connection, "acc-2", member), Person(connection, "acc-3", null));
        var rows = await m.RowsAsync("integration_people");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => m.MigrateAsync(Migration));

        Assert.Contains("integration_people: 1 members are linked to more than one person of a connection", error.Message, StringComparison.Ordinal);
        Assert.Equal(rows, await m.RowsAsync("integration_people"));
        Assert.Null(await m.IndexAsync("ux_integration_people_user"));
        Assert.False(await m.AppliedAsync(Migration));
    }

    [Fact]
    public async Task Clean_links_get_the_index_unlinked_persons_stay_free_and_a_second_link_is_refused()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var member = await m.SeedUserAsync("linked@mail.com", "linked@mail.com");
        var other = await m.SeedUserAsync("other@mail.com", "other@mail.com");
        var connection = await m.SeedConnectionAsync(member);
        var second = await m.SeedConnectionAsync(member);
        // The same member on two connections, another member, and two persons nobody is linked to: all allowed.
        await SeedPeopleAsync(m, Person(connection, "acc-1", member), Person(second, "acc-1", member), Person(connection, "acc-2", other),
            Person(connection, "acc-3", null), Person(connection, "acc-4", null));
        var rows = await m.RowsAsync("integration_people");

        await m.MigrateAsync(Migration);

        Assert.Equal(rows, await m.RowsAsync("integration_people"));
        Assert.Equal(
            "CREATE UNIQUE INDEX ux_integration_people_user ON public.integration_people USING btree (connection_id, user_id) WHERE (user_id IS NOT NULL)",
            await m.IndexAsync("ux_integration_people_user"));
        await SeedPeopleAsync(m, Person(connection, "acc-5", null));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => SeedPeopleAsync(m, Person(connection, "acc-6", member)));
        Assert.Contains("ux_integration_people_user", refused.Message, StringComparison.Ordinal);

        await m.MigrateAsync(Before);
        Assert.Null(await m.IndexAsync("ux_integration_people_user"));
    }
}
