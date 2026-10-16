namespace ClaudeMonitor.Agent.Config;

/// <summary>The settings of the push through a session's inbox socket (ADR-0007).</summary>
public sealed partial record AgentConfig
{
    /// <summary>"inbox" (default): the daemon writes the prompt to the session's socket. "channel": the MCP process sends a Claude Code channel notification (ADR-0003).</summary>
    public string PushTransport { get; init; } = PushTransports.Inbox;

    /// <summary>
    /// The socket (macOS/Linux) or named pipe (Windows) Claude Code exported to this process as CLAUDE_CODE_MESSAGING_SOCKET: only a hook
    /// started by a session has it. Null everywhere else.
    /// </summary>
    public string? MessagingSocket { get; init; }

    /// <summary>CLAUDE_CODE_MESSAGING_TOKEN, the session's key for its own socket. A secret: kept in the user-only database, never uploaded, never logged.</summary>
    public string? MessagingToken { get; init; }

    /// <summary>How long the daemon waits to connect to a session's socket; a socket that does not answer is a session that is gone.</summary>
    public TimeSpan InboxConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the daemon re-reads the saved push settings, so `cm-agent install --push on` needs no daemon restart.</summary>
    public TimeSpan PushSettingsEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True when push is on and goes through the inbox socket (the daemon's job).</summary>
    public bool InboxPushEnabled => PushEnabled && PushTransport == PushTransports.Inbox;

    /// <summary>True when push is on and goes through a channel (the MCP process's job).</summary>
    public bool ChannelPushEnabled => PushEnabled && PushTransport == PushTransports.Channel;
}
