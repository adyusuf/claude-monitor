using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-066: <c>integration_links.missed_passes</c> counts the reconcile passes in a row that missed a mirrored space, sprint or issue; existing links start at 0.</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorMissedPassesMigrationTests(ApiFactory api)
{
    private const string Before = "20261116065730_InvitationEmail";
    private const string Migration = "20261116065942_MirrorMissedPasses";

    [Fact]
    public async Task Existing_links_start_at_zero_a_new_link_defaults_to_zero_and_a_roll_back_drops_the_column()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var connection = await m.SeedConnectionAsync(await m.SeedUserAsync("connector@mail.com", "connector@mail.com"));
        await m.ExecAsync($"""
            INSERT INTO integration_links (id, connection_id, kind, external_id, local_id, synced_at)
            VALUES ('{Guid.NewGuid()}', '{connection}', 'issue', '10001', '{Guid.NewGuid()}', now()),
                   ('{Guid.NewGuid()}', '{connection}', 'project', '20001', '{Guid.NewGuid()}', now())
            """);

        await m.MigrateAsync(Migration);

        Assert.Equal("NO", await m.ScalarAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'integration_links' AND column_name = 'missed_passes'"));
        Assert.Equal(2, await m.CountAsync("SELECT count(*) FROM integration_links WHERE missed_passes = 0"));
        await m.ExecAsync($"INSERT INTO integration_links (id, connection_id, kind, external_id, local_id, synced_at) VALUES ('{Guid.NewGuid()}', '{connection}', 'issue', '10002', '{Guid.NewGuid()}', now())");
        Assert.Equal(0, await m.CountAsync("SELECT count(*) FROM integration_links WHERE missed_passes <> 0"));

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.ColumnCountAsync("integration_links", "missed_passes"));
        Assert.Equal(3, await m.CountAsync("SELECT count(*) FROM integration_links"));
    }
}
