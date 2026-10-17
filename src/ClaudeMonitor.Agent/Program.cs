using ClaudeMonitor.Agent;
using ClaudeMonitor.Agent.Config;

// cm-agent: hook | mcp | daemon | login | logout | install | uninstall | status | version  (ADR-0002)
var config = AgentConfig.FromEnvironment();
TextReader stdin = Console.In;
TextWriter stdout = Console.Out;
if (args.FirstOrDefault() == "hook")
{
    (stdin, stdout) = HookStreams.Open(Console.OpenStandardInput(), Console.OpenStandardOutput()); // JSON is UTF-8 whatever the console code page is
}

return await Cli.RunAsync(args, config, stdin, stdout, Console.Error, TimeProvider.System);
