using ClaudeMonitor.Agent.Storage;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Tests;

public sealed class LocalStoreMigrationTests
{
    [Fact]
    public void A_database_made_before_question_answers_gets_the_column_and_keeps_its_rows()
    {
        using var home = new TempHome();
        using (var old = new SqliteConnection($"Data Source={home.Config.DatabasePath};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE permissions (local_id TEXT PRIMARY KEY, harness TEXT NOT NULL, session TEXT NOT NULL,
                    tool_name TEXT NOT NULL, tool_input TEXT NOT NULL, wait_seconds INTEGER NOT NULL, created_at TEXT NOT NULL,
                    remote_id TEXT, decision TEXT, reason TEXT, state TEXT NOT NULL);
                INSERT INTO permissions VALUES ('l1', 'claude_code', 's1', 'Bash', '{}', 60, '2026-10-03T12:00:00.0000000+00:00', 'r1', NULL, NULL, 'sent');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var store = new LocalStore(home.Config.DatabasePath))
        {
            Assert.Equal("Bash", store.Permission("l1")!.ToolName);
            store.PermissionAnswered("r1", "allow", null, "{\"q\":\"a\"}");
            Assert.Equal("{\"q\":\"a\"}", store.Permission("l1")!.Answers);
        }

        using var again = new LocalStore(home.Config.DatabasePath); // opening it a second time finds the column and leaves it
        Assert.Equal("allow", again.Permission("l1")!.Decision);
    }
}
