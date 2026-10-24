using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Podium.Api.Tests;

/// <summary>Which failures of a save are "someone else saved" (409) and which are real errors (500, logged), ADR-0017: only a unique-key violation or a concurrency conflict is a 409.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsFailureTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    /// <summary>The save of a setting fails with this error (once armed) before it reaches the database.</summary>
    private sealed class FailSettingsSave(Func<Exception> make) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ServerSetting>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)) throw make();
            return ValueTask.FromResult(result);
        }
    }

    private static DbUpdateException Failure(string kind) => kind switch
    {
        "plain" => new DbUpdateException("the save failed"),
        "check" => new DbUpdateException("check", new PostgresException("check violated", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)),
        "foreign" => new DbUpdateException("fk", new PostgresException("key not present", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation)),
        "lookalike" => new DbUpdateException("lookalike", new InvalidOperationException("23505 duplicate key value violates unique constraint")),
        "unique" => new DbUpdateException("dup", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)),
        "concurrency" => new DbUpdateConcurrencyException("someone else changed it"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private async Task<(int Rows, int Audited)> CountsAsync(TestUser admin)
    {
        await using var db = api.Db();
        return (await db.ServerSettings.CountAsync(), await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("check")]
    [InlineData("foreign")]
    [InlineData("lookalike")]
    public async Task A_failed_save_that_is_not_a_key_clash_is_a_logged_500_and_saves_and_audits_nothing(string kind)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sf-500-" + kind);
        var log = new TracingLog();
        await using var host = HostKit.WithInterceptor(api, new FailSettingsSave(() => Failure(kind)), log);
        var client = await HostKit.SignInAsync(api, host, admin);

        var response = await ServerAdminKit.PutAsync(client, "smtp", new { host = "mine.example.net" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Error && e.Category.Contains("ExceptionHandler", StringComparison.Ordinal)); // the failure is not swallowed
        Assert.Equal((0, 0), await CountsAsync(admin));
    }

    [Theory]
    [InlineData("unique")]
    [InlineData("concurrency")]
    public async Task A_unique_key_violation_or_a_concurrency_conflict_is_a_409_settings_conflict(string kind)
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sf-409-" + kind);
        await using var host = HostKit.WithInterceptor(api, new FailSettingsSave(() => Failure(kind)));
        var client = await HostKit.SignInAsync(api, host, admin);

        var response = await ServerAdminKit.PutAsync(client, "smtp", new { host = "mine.example.net" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("settings_conflict", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal((0, 0), await CountsAsync(admin));
    }
}
