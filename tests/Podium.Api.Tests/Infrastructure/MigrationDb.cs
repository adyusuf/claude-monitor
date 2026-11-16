using System.Globalization;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// A database of its own in the test container for one migration test: it is migrated to the step before the migration under test,
/// seeded with plain SQL against that older schema, and dropped afterwards. The shared database of the other tests is never moved.
/// </summary>
public sealed class MigrationDb : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string name;

    private MigrationDb(string adminConnection, string name, PodiumDb db)
    {
        this.adminConnection = adminConnection;
        this.name = name;
        Db = db;
    }

    public PodiumDb Db { get; }

    public static async Task<MigrationDb> CreateAsync(ApiFactory api, string migratedTo)
    {
        ArgumentNullException.ThrowIfNull(api);
        string adminConnection;
        await using (var shared = api.Db())
        {
            adminConnection = shared.Database.GetConnectionString()!;
        }

        var name = "mig_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var own = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name, Pooling = false }.ConnectionString;
        var db = new PodiumDb(new DbContextOptionsBuilder<PodiumDb>().UseNpgsql(own).Options);
        var result = new MigrationDb(adminConnection, name, db);
        await result.MigrateAsync(migratedTo);
        return result;
    }

    public Task MigrateAsync(string target) => Db.GetService<IMigrator>().MigrateAsync(target);

    public async Task<bool> AppliedAsync(string migration) => (await Db.Database.GetAppliedMigrationsAsync()).Contains(migration);

    public Task ExecAsync(string sql) => Db.Database.ExecuteSqlRawAsync(sql);

    public async Task<object?> ScalarAsync(string sql)
    {
        var connection = Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public async Task<long> CountAsync(string sql) => Convert.ToInt64(await ScalarAsync(sql), CultureInfo.InvariantCulture);

    /// <summary>Every row of a table as text, in a fixed order: equal before and after means no row was changed, added or removed.</summary>
    public async Task<string> RowsAsync(string table) =>
        (string?)await ScalarAsync($"SELECT coalesce(string_agg(t::text, '|' ORDER BY t::text), '') FROM {table} t") ?? "";

    public Task<long> ColumnCountAsync(string table, string column) =>
        CountAsync($"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'");

    /// <summary>The definition of an index as PostgreSQL keeps it, or null when there is none.</summary>
    public async Task<string?> IndexAsync(string index) => (string?)await ScalarAsync($"SELECT indexdef FROM pg_indexes WHERE indexname = '{index}'");

    /// <summary>A user row on the older schema; <paramref name="normalized"/> is given apart so a test can seed what the API would not write.</summary>
    public async Task<Guid> SeedUserAsync(string email, string normalized)
    {
        var id = Guid.NewGuid();
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, email_normalized, display_name, display_name_search, status, created_at, updated_at, failed_sign_ins)
            VALUES ({id}, {email}, {normalized}, 'Seeded', 'seeded', 'active', now(), now(), 0)
            """);
        return id;
    }

    /// <summary>A workspace owned by <paramref name="owner"/> with one Jira connection in it; returns the connection.</summary>
    public async Task<Guid> SeedConnectionAsync(Guid owner)
    {
        var workspace = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var host = $"site-{connection:N}.atlassian.net";
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO workspaces (id, name, name_search, created_by, created_at, status) VALUES ({workspace}, 'Seeded', 'seeded', {owner}, now(), 'active')
            """);
        await Db.Database.ExecuteSqlAsync($"""
            INSERT INTO integration_connections (id, workspace_id, provider, site_host, cloud_id, connector_user_id, auto_invite, status, created_by, created_at, updated_at)
            VALUES ({connection}, {workspace}, 'jira', {host}, 'cloud', {owner}, false, 'active', {owner}, now(), now())
            """);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
