using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>F-188: <c>permission_requests.client_key</c> holds 1 to 100 characters (<c>ck_permission_requests_client_key</c>), the cap of the ask keys; NULL stays allowed.</summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionRequestKeyLengthMigrationTests(ApiFactory api)
{
    private const string Before = "20261116072641_EmailKeyTrigger";
    private const string Migration = "20261116073003_PermissionRequestKeyLength";
    private const string Check = "ck_permission_requests_client_key";

    [Fact]
    public async Task The_check_is_added_and_a_roll_back_drops_it()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);

        await m.MigrateAsync(Migration);

        Assert.Equal("CHECK (((client_key IS NULL) OR ((length(client_key) >= 1) AND (length(client_key) <= 100))))",
            await m.ScalarAsync($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = '{Check}'"));

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.CountAsync($"SELECT count(*) FROM pg_constraint WHERE conname = '{Check}'"));
    }

    [Fact]
    public async Task A_key_of_100_characters_is_kept_and_101_or_none_is_refused()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "key-length");
        var id = (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" })).Id;
        await using var db = api.Db();
        var longest = new string('k', 100);
        var tooLong = new string('k', 101);

        await db.Database.ExecuteSqlAsync($"UPDATE permission_requests SET client_key = {longest} WHERE id = {id}");
        var refusedLong = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"UPDATE permission_requests SET client_key = {tooLong} WHERE id = {id}"));
        var refusedEmpty = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync($"UPDATE permission_requests SET client_key = '' WHERE id = {id}"));

        Assert.Contains(Check, refusedLong.Message, StringComparison.Ordinal);
        Assert.Contains(Check, refusedEmpty.Message, StringComparison.Ordinal);
        Assert.Equal(longest, (await db.PermissionRequests.AsNoTracking().FirstAsync(p => p.Id == id)).ClientKey);
    }
}
