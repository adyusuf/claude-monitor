using Aipod.Tray;
using Podium.Agent.Auth;
using Podium.Agent.Config;

namespace Aipod.Tray.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>An agent home in a temp folder, with helpers to make it look connected.</summary>
public sealed class AgentHome : IDisposable
{
    public AgentHome(Func<string, string?>? extra = null)
    {
        Path = Directory.CreateTempSubdirectory("aipod-tray-").FullName;
        Config = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? Path : extra?.Invoke(k));
    }

    public string Path { get; }

    public AgentConfig Config { get; }

    public void Connect(string server = "https://aipod.example.test")
    {
        (Identity.Load(Config) with { Server = server, AgentId = Guid.NewGuid() }).Save(Config);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // a file still held by a test that ended badly: the temp folder is cleaned by the system later
        }
    }
}

public sealed class FakeCli : IAgentCli
{
    public List<string[]> Runs { get; } = [];

    public List<string> Opened { get; } = [];

    public Func<string[], Task<CliResult>> Answer { get; set; } = _ => Task.FromResult(new CliResult(0, ""));

    public Exception? OpenFails { get; set; }

    public Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Runs.Add([.. args]);
        return Answer([.. args]);
    }

    public void Open(string target)
    {
        if (OpenFails is not null) throw OpenFails;
        Opened.Add(target);
    }
}
