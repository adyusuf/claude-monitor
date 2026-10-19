using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// ADR-0011: the table of reported work folders was renamed from "projects" to "repos". The migration must keep the rows and the
/// link from a session, and go back, because the managed "projects" table took the old name.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RepoRenameMigrationTests(ApiFactory api)
{
    private const string Before = "20261018133428_AgentActions";
    private const string Rename = "20261019093347_RenameProjectsToRepos";

    private static async Task<long> CountAsync(MonitorDb db, string sql)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task The_rename_keeps_the_rows_and_the_session_link_and_a_roll_back_restores_the_old_names()
    {
        var user = await api.NewClient().SignedUpAsync("rn-user");
        var agent = await user.ConnectAgentAsync();
        Assert.Equal(HttpStatusCode.OK, (await agent.SendAsync(TestAgent.Hook("s-rename", "SessionStart", new { }, DateTimeOffset.UtcNow, "rename-key"))).StatusCode);

        await using var db = api.Db();
        var migrator = db.GetService<IMigrator>();
        Assert.Equal(1, await CountAsync(db, "SELECT count(*) FROM repos WHERE key = 'rename-key'"));
        var sessionLink = $"SELECT count(*) FROM harness_sessions s JOIN repos r ON r.id = s.repo_id WHERE r.key = 'rename-key'";
        Assert.Equal(1, await CountAsync(db, sessionLink));

        try
        {
            await migrator.MigrateAsync(Before);
            Assert.Equal(1, await CountAsync(db, "SELECT count(*) FROM projects WHERE key = 'rename-key'"));
            Assert.Equal(1, await CountAsync(db, "SELECT count(*) FROM harness_sessions s JOIN projects p ON p.id = s.project_id WHERE p.key = 'rename-key'"));
            Assert.Equal(0, await CountAsync(db, "SELECT count(*) FROM pg_class WHERE relname = 'repos'"));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(1, await CountAsync(db, sessionLink));
        Assert.Equal(0, await CountAsync(db, "SELECT count(*) FROM projects"));
        Assert.Equal(3, await CountAsync(db, "SELECT count(*) FROM pg_constraint WHERE conname IN ('pk_repos', 'fk_repos_workspaces_workspace_id', 'fk_harness_sessions_repos_repo_id')"));
        Assert.Contains(Rename, await db.Database.GetAppliedMigrationsAsync());
    }
}
