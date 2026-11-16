using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-004: both active-sprint unique indexes leave archived sprints out, so a sprint left active with <c>archived_at</c> set (a start racing a
/// delete) stops holding its scope's slot. The roll-back changes nothing when a scope would break the old filter; it stops and names the count.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class SprintActiveIndexArchivedMigrationTests(ApiFactory api)
{
    private const string Before = "20261116070942_PermissionRequestAnswers";
    private const string Migration = "20261116071141_SprintActiveIndexArchived";

    private static async Task<(Guid Workspace, Guid Project, Guid Owner)> SeedProjectAsync(MigrationDb m)
    {
        var owner = await m.SeedUserAsync("sprint-owner@mail.com", "sprint-owner@mail.com");
        var connection = await m.SeedConnectionAsync(owner);
        var workspace = (Guid)(await m.ScalarAsync($"SELECT workspace_id FROM integration_connections WHERE id = '{connection}'"))!;
        var project = Guid.NewGuid();
        await m.ExecAsync($"""
            INSERT INTO projects (id, workspace_id, name, name_search, owner_user_id, item_types, priority_scale, estimation_unit, sprint_length_days,
                definition_of_ready, definition_of_done, status, created_by, updated_by, created_at, updated_at)
            VALUES ('{project}', '{workspace}', 'Seeded', 'seeded', '{owner}', ARRAY['task'], 'p0_p3', 'story_points', 14, ARRAY[]::text[], ARRAY[]::text[],
                'active', '{owner}', '{owner}', now(), now())
            """);
        return (workspace, project, owner);
    }

    private static Task SprintAsync(MigrationDb m, (Guid Workspace, Guid Project, Guid Owner) p, int number, bool archived) => m.ExecAsync($"""
        INSERT INTO sprints (id, workspace_id, project_id, number, status, archived_at, created_by, updated_by, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{p.Workspace}', '{p.Project}', {number}, 'active', {(archived ? "now()" : "NULL")}, '{p.Owner}', '{p.Owner}', now(), now())
        """);

    [Fact]
    public async Task An_archived_active_sprint_no_longer_holds_the_slot_a_second_live_one_still_cannot_and_a_clean_roll_back_restores_the_old_filters()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var p = await SeedProjectAsync(m);
        await SprintAsync(m, p, 1, archived: true);
        Assert.Contains("ux_sprints_active_project", (await Assert.ThrowsAnyAsync<Exception>(() => SprintAsync(m, p, 2, archived: false))).Message, StringComparison.Ordinal);

        await m.MigrateAsync(Migration);

        await SprintAsync(m, p, 2, archived: false);
        Assert.Contains("ux_sprints_active_project", (await Assert.ThrowsAnyAsync<Exception>(() => SprintAsync(m, p, 3, archived: false))).Message, StringComparison.Ordinal);
        Assert.EndsWith("WHERE ((status = 'active'::text) AND (application_id IS NULL) AND (source_connection_id IS NULL) AND (archived_at IS NULL))",
            await m.IndexAsync("ux_sprints_active_project"), StringComparison.Ordinal);
        Assert.EndsWith("WHERE ((status = 'active'::text) AND (application_id IS NOT NULL) AND (source_connection_id IS NULL) AND (archived_at IS NULL))",
            await m.IndexAsync("ux_sprints_active_application"), StringComparison.Ordinal);

        await m.ExecAsync($"UPDATE sprints SET status = 'closed' WHERE project_id = '{p.Project}' AND archived_at IS NOT NULL");
        await m.MigrateAsync(Before);
        Assert.DoesNotContain("archived_at", await m.IndexAsync("ux_sprints_active_project"), StringComparison.Ordinal);
        Assert.DoesNotContain("archived_at", await m.IndexAsync("ux_sprints_active_application"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_roll_back_that_would_break_the_old_filter_stops_and_changes_nothing()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        var p = await SeedProjectAsync(m);
        await m.MigrateAsync(Migration);
        await SprintAsync(m, p, 1, archived: true);
        await SprintAsync(m, p, 2, archived: false);
        var rows = await m.RowsAsync("sprints");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => m.MigrateAsync(Before));

        Assert.Contains("sprints: 1 projects or applications have an archived active sprint next to their active one", error.Message, StringComparison.Ordinal);
        Assert.Equal(rows, await m.RowsAsync("sprints"));
        Assert.Contains("archived_at IS NULL", await m.IndexAsync("ux_sprints_active_project"), StringComparison.Ordinal);
        Assert.True(await m.AppliedAsync(Migration));
    }
}
