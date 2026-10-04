using ClaudeMonitor.Agent;
using ClaudeMonitor.Agent.Config;

// cm-agent: hook | mcp | daemon | login | logout | install | uninstall | status | version  (ADR-0002)
var config = AgentConfig.FromEnvironment();
return await Cli.RunAsync(args, config, Console.In, Console.Out, Console.Error, TimeProvider.System);
