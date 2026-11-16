using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>F-196: <c>permission_requests.answers</c> (jsonb, nullable) keeps the options the owner chose, so the agent's poll can return them.</summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionRequestAnswersMigrationTests(ApiFactory api)
{
    private const string Before = "20261116070708_PermissionRequestKey";
    private const string Migration = "20261116070942_PermissionRequestAnswers";

    [Fact]
    public async Task The_column_is_added_as_nullable_jsonb_and_a_roll_back_drops_it()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);

        await m.MigrateAsync(Migration);

        Assert.Equal("jsonb YES", await m.ScalarAsync(
            "SELECT data_type || ' ' || is_nullable FROM information_schema.columns WHERE table_name = 'permission_requests' AND column_name = 'answers'"));

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.ColumnCountAsync("permission_requests", "answers"));
    }

    [Fact]
    public async Task A_request_is_made_without_answers_and_answers_written_are_read_back_as_json()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "answers");
        var id = (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" })).Id;
        await using (var db = api.Db())
        {
            var row = await db.PermissionRequests.FirstAsync(p => p.Id == id);
            Assert.Null(row.Answers);
            row.Answers = JsonSerializer.Serialize(new Dictionary<string, string> { ["Which branch?"] = "main" });
            await db.SaveChangesAsync();
        }

        await using var again = api.Db();
        Assert.Equal("main", await again.Database.SqlQuery<string>($"SELECT answers ->> 'Which branch?' AS \"Value\" FROM permission_requests WHERE id = {id}").SingleAsync());
    }
}
