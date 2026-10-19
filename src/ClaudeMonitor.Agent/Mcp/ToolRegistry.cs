using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ClaudeMonitor.Agent.Mcp;

/// <summary>
/// Registers the MCP tools under the names of a mode (ADR-0012 step 3). The old names are the attributes on the tool methods and are registered the way
/// they always were; the aipod names are the same methods created under another name, with the names inside their descriptions (and their parameters'
/// descriptions) rewritten, so each mode lists exactly one set and no tool is ever listed twice.
/// </summary>
public static class ToolRegistry
{
    public static readonly IReadOnlyList<Type> ToolTypes =
        [typeof(MonitorTools), typeof(MachineTools), typeof(RemoteTools), typeof(ScriptTools), typeof(AskTools), typeof(GrantTools), typeof(InboxTools)];

    public static IMcpServerBuilder AddTools(this IMcpServerBuilder builder, AgentConfig config, TimeProvider clock, ToolNameMode mode)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (mode == ToolNameMode.Legacy)
        {
            return builder.WithTools<MonitorTools>().WithTools<MachineTools>().WithTools<RemoteTools>().WithTools<ScriptTools>().WithTools<AskTools>()
                .WithTools<GrantTools>().WithTools<InboxTools>();
        }

        return builder.WithTools(Create(config, clock, mode));
    }

    /// <summary>One tool per tool method, named for <paramref name="mode"/>. A fresh instance of its class serves each call, as the SDK does for the attribute route.</summary>
    public static IReadOnlyList<McpServerTool> Create(AgentConfig config, TimeProvider clock, ToolNameMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        var tools = new List<McpServerTool>();
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } attribute) continue;
                var name = McpToolNames.Text(mode, attribute.Name ?? method.Name);
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var options = new McpServerToolCreateOptions
                {
                    Name = name,
                    Description = description is null ? null : McpToolNames.Text(mode, description),
                    Destructive = attribute.Destructive,
                    ReadOnly = attribute.ReadOnly,
                    Idempotent = attribute.Idempotent,
                    OpenWorld = attribute.OpenWorld,
                };
                var tool = McpServerTool.Create(method, _ => Activator.CreateInstance(type, config, clock)!, options);
                var schema = McpToolNames.Text(mode, tool.ProtocolTool.InputSchema.GetRawText()); // the parameters' descriptions
                tool.ProtocolTool.InputSchema = JsonDocument.Parse(schema).RootElement.Clone();
                tools.Add(tool);
            }
        }

        return tools;
    }
}
