using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>An approval key's name is text its owner typed (F-042): it is in the account's export, and deleting the account removes it.</summary>
[Collection(ApiGroup.Name)]
public sealed class PrivacyApprovalKeyTests(ApiFactory api)
{
    private const string KeyName = "private-laptop-name";

    private async Task SeedKeyAsync(Guid userId, string name, DateTimeOffset? retiredAt)
    {
        await using var db = api.Db();
        db.ApprovalKeys.Add(new ApprovalKey
        {
            UserId = userId,
            Name = name,
            PublicKey = "AQID",
            Fingerprint = Guid.NewGuid().ToString("N"),
            CreatedAt = api.Clock.GetUtcNow(),
            RetiredAt = retiredAt,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_export_lists_the_users_approval_keys_live_and_retired_and_nobody_elses()
    {
        var user = await api.NewClient().SignedUpAsync("export-keys");
        var other = await api.NewClient().SignedUpAsync("export-keys-other");
        await SeedKeyAsync(user.Id, KeyName, null);
        await SeedKeyAsync(user.Id, KeyName + "-old", api.Clock.GetUtcNow());
        await SeedKeyAsync(other.Id, "someone-elses-key", null);

        var response = await user.Http.GetAsync("/api/me/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var names = export.GetProperty("approvalKeys").EnumerateArray().Select(k => k.GetProperty("name").GetString()).Order().ToList();
        Assert.Equal([KeyName, KeyName + "-old"], names);
    }

    [Fact]
    public async Task Deleting_the_account_removes_its_approval_keys_and_leaves_other_peoples()
    {
        var user = await api.NewClient().SignedUpAsync("delete-keys");
        var other = await api.NewClient().SignedUpAsync("delete-keys-other");
        await SeedKeyAsync(user.Id, KeyName, null);
        await SeedKeyAsync(other.Id, "someone-elses-key", null);

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        await using var db = api.Db();
        Assert.Empty(await db.ApprovalKeys.AsNoTracking().Where(k => k.UserId == user.Id).ToListAsync());
        Assert.Single(await db.ApprovalKeys.AsNoTracking().Where(k => k.UserId == other.Id).ToListAsync());
    }
}
