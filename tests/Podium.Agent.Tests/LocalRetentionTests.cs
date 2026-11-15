using Microsoft.Data.Sqlite;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>Captured content is deleted after the retention: prompts that were never applied and permission inputs too, not only applied prompts.</summary>
public sealed class LocalRetentionTests : IDisposable
{
    private readonly RemoteFixture fx = new();
    private readonly RemoteRelay relay;

    public LocalRetentionTests() => relay = new RemoteRelay(fx.Home.Config, fx.Store, fx.Api, fx.Clock);

    public void Dispose() => fx.Dispose();

    private DateTimeOffset Now => fx.Clock.GetUtcNow();

    private TimeSpan Retention => fx.Home.Config.RemoteLocalRetention;

    /// <summary>The ids of the commands that still hold their text, read straight from the file.</summary>
    private List<string> CommandsWithText()
    {
        using var connection = new SqliteConnection($"Data Source={fx.Home.Config.DatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM commands WHERE body IS NOT NULL ORDER BY id";
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private void Ask(string id, DateTimeOffset at) => fx.Store.AddPermission(new PermissionAsk(id, HarnessKinds.ClaudeCode, "s-1", "Bash",
        """{"command":"deploy"}""", 60, at, null, null, null, PermissionStates.New));

    [Fact]
    public async Task Retention_deletes_expired_unread_command_bodies_and_old_permission_inputs()
    {
        var old = Now - Retention - TimeSpan.FromHours(1);
        fx.Store.SaveCommand(new LocalCommand("expired-pushed", "s-1", CommandKinds.Prompt, "pushed, never confirmed", old.AddMinutes(-30)));
        Assert.Single(fx.Store.TakePromptsForPush("s-1", CommandKinds.Prompt, old.AddMinutes(-31))); // pushed while it was still valid
        fx.Store.SaveCommand(new LocalCommand("expired-queued", "s-1", CommandKinds.Prompt, "never taken", old));
        fx.Store.SaveCommand(new LocalCommand("expired-recently", "s-1", CommandKinds.Prompt, "inside the retention", Now - TimeSpan.FromHours(1)));
        fx.Store.SaveCommand(new LocalCommand("waiting", "s-1", CommandKinds.Prompt, "still valid", Now.AddMinutes(30)));
        Ask("old-ask", old);
        Ask("recent-ask", Now - Retention + TimeSpan.FromHours(1));

        await relay.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["expired-recently", "waiting"], CommandsWithText());
        Assert.Null(fx.Store.Permission("old-ask"));
        Assert.NotNull(fx.Store.Permission("recent-ask"));
        Assert.Equal("waiting", fx.Store.TakeCommand("s-1", CommandKinds.Prompt, Now)!.Id); // the rows that stay still work
    }
}
