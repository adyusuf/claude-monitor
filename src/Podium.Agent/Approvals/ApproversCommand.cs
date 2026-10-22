namespace Podium.Agent.Approvals;

/// <summary>
/// <c>aipod approvers add &lt;public key&gt; [--name n] | remove &lt;fingerprint&gt; | list</c> (ADR-0010 §4): the admin of a machine pins the owner keys it will trust.
/// The key and its fingerprint come from the web's account page; the admin compares the fingerprint printed here with the one on the page. The agent's own account
/// cannot write the file on a service machine, so these verbs run as the admin.
/// </summary>
public static class ApproversCommand
{
    public static async Task<int> RunAsync(string[] args, Config.AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            if (args.Length >= 3 && args[1] == "add")
            {
                var name = Option(args, "--name");
                var id = ApproversStore.Add(config, args[2], name);
                await stdout.WriteLineAsync($"pinned {id}");
                await stdout.WriteLineAsync("Compare this fingerprint with the one the web shows for the key before you rely on it.");
                var policy = ApproversStore.Load(config);
                await stdout.WriteLineAsync(policy.Required ? "Signed approvals are required here." : "Signed approvals are not required yet: aipod config approvals required");
                return 0;
            }

            if (args.Length == 3 && args[1] == "remove")
            {
                if (!ApproversStore.Remove(config, args[2]))
                {
                    await stderr.WriteLineAsync($"No pinned key has the fingerprint {args[2]}.");
                    return 1;
                }

                await stdout.WriteLineAsync("removed");
                return 0;
            }

            if (args.Length == 2 && args[1] == "list")
            {
                var policy = ApproversStore.Load(config);
                await stdout.WriteLineAsync(ApproversStore.Describe(policy));
                foreach (var key in policy.Keys) await stdout.WriteLineAsync($"{key.Id}  {key.Name}");
                return 0;
            }
        }
        catch (ArgumentException e)
        {
            await stderr.WriteLineAsync(e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"The approvers file could not be written ({e.GetType().Name}); on a service machine run this as the admin.");
            return 1;
        }

        await stderr.WriteLineAsync("usage: aipod approvers add <public key> [--name <name>] | remove <fingerprint> | list");
        return 2;
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
