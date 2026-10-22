using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Podium.Agent.Mcp;

/// <summary>Registers the MCP tools (ADR-0016). Their names are the attributes on the tool methods: <c>aipod_&lt;name&gt;</c>.</summary>
public static class ToolRegistry
{
    public static readonly IReadOnlyList<Type> ToolTypes =
        [typeof(AipodTools), typeof(MachineTools), typeof(RemoteTools), typeof(ScriptTools), typeof(AskTools), typeof(GrantTools), typeof(InboxTools)];

    public static IMcpServerBuilder AddTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithTools<AipodTools>().WithTools<MachineTools>().WithTools<RemoteTools>().WithTools<ScriptTools>().WithTools<AskTools>()
            .WithTools<GrantTools>().WithTools<InboxTools>();
    }
}
