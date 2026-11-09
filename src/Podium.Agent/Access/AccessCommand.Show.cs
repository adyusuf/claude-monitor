using Podium.Agent.Config;

namespace Podium.Agent.Access;

// `aipod config access show`: the ceiling first, then what each web policy is on this machine.
public static partial class AccessCommand
{
    private static async Task<int> ShowAsync(AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        if (AccessCeilingFile.Load(config) is not { } loaded)
        {
            await stderr.WriteLineAsync(Strings.T(Msg.AccessNoServiceFile, config.ExecConfigPath));
            return 1;
        }

        var (ceiling, policy) = loaded;
        var source = Strings.T(AccessCeilingFile.IsService(config) ? Msg.AccessSourceService : Msg.AccessSourceAgent);
        await stdout.WriteLineAsync(Strings.T(Msg.AccessShowCeiling, source, policy.Level, Strings.T(policy.CeilingAllowsShell ? Msg.AccessYes : Msg.AccessNo)));
        await stdout.WriteLineAsync(ceiling.AllowedRoots.Count == 0 ? Strings.T(Msg.AccessShowAllowedNone) : Strings.T(Msg.AccessShowAllowed, ceiling.AllowedRoots.Count));
        foreach (var folder in ceiling.AllowedRoots) await stdout.WriteLineAsync("  + " + folder);
        await stdout.WriteLineAsync(Strings.T(Msg.AccessShowDenied, ceiling.DeniedRoots.Count));
        foreach (var folder in ceiling.DeniedRoots) await stdout.WriteLineAsync("  - " + folder);
        await stdout.WriteLineAsync(ceiling.Executables.Count == 0 ? Strings.T(Msg.AccessShowAppsNone) : Strings.T(Msg.AccessShowApps, ceiling.Executables.Count));
        foreach (var app in ceiling.Executables) await stdout.WriteLineAsync("  * " + app.Path + (app.Sha256 is { } sha ? " (SHA-256 " + sha + ")" : ""));
        foreach (var line in AccessReports.DescribePolicies(config, policy)) await stdout.WriteLineAsync(line);
        return 0;
    }
}
