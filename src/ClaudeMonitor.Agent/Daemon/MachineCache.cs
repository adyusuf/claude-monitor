using System.Globalization;
using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The daemon's copy of the workspace's machines for the session-start block (ADR-0008 §3b): read from the API every
/// <see cref="AgentConfig.MachineCacheEvery"/>, rendered by <see cref="MachineContext"/> and kept in the local database where the
/// synchronous SessionStart hook reads it without any network. Nothing is kept while remote runs are off for the workspace, while
/// the block is off locally, or while this is the only machine. An API that does not know the call (404) switches it off until restart.
/// </summary>
public sealed class MachineCache(AgentConfig config, LocalStore store, ApiClient api, TimeProvider clock)
{
    private bool unsupported;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (unsupported) return;
        if (!config.MachinesContextEnabled || store.Get(MachineMonitor.RemoteRunsKey) != "true")
        {
            Clear();
            return;
        }

        List<MachineView> machines;
        try
        {
            var json = await api.RemoteAsync(HttpMethod.Get, "api/agent/machines", null, ct);
            machines = json.Deserialize<List<MachineView>>(ApiClient.Json) ?? [];
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.NotFound)
        {
            unsupported = true;
            Clear();
            return;
        }

        Store(machines);
    }

    /// <summary>Renders and keeps the block for these machines (the API's answer, or a test's); clears it when there is nothing to tell.</summary>
    public void Store(IReadOnlyList<MachineView> machines)
    {
        var text = MachineContext.Render(machines, Identity.Peek(config)?.AgentId, config.MachinesContextMax);
        if (text is null)
        {
            Clear();
            return;
        }

        store.SetMany([(AgentConfig.MachinesContextKey, text), (AgentConfig.MachinesContextAtKey, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))]);
    }

    private void Clear()
    {
        store.Remove(AgentConfig.MachinesContextKey);
        store.Remove(AgentConfig.MachinesContextAtKey);
    }
}
