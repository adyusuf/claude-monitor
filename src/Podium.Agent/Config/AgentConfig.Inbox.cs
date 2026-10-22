namespace Podium.Agent.Config;

/// <summary>The settings of the push through a session's inbox socket (ADR-0007).</summary>
public sealed partial record AgentConfig
{
    /// <summary>"channel" (default, what push always was): the MCP process sends a Claude Code channel notification (ADR-0003). "inbox": the daemon writes the prompt to the session's socket (ADR-0007).</summary>
    public string PushTransport { get; init; } = PushTransports.Channel;

    /// <summary>"short" (default): a one-line notice, the message is read with aipod_inbox_read (ADR-0007, A2). "full": the inbox gets the notice, the wrapper and the message.</summary>
    public string InboxFormat { get; init; } = InboxFormats.Brief;

    /// <summary>True when AP_INBOX_FORMAT was set: it then wins over the value `aipod config push-format` saved.</summary>
    public bool InboxFormatFromEnvironment { get; init; }

    /// <summary>
    /// The socket (macOS/Linux) or named pipe (Windows) Claude Code exported to this process as CLAUDE_CODE_MESSAGING_SOCKET: only a hook
    /// started by a session has it. Null everywhere else.
    /// </summary>
    public string? MessagingSocket { get; init; }

    /// <summary>CLAUDE_CODE_MESSAGING_TOKEN, the session's key for its own socket. A secret: kept in the user-only database, never uploaded, never logged.</summary>
    public SecretText? MessagingToken { get; init; }

    /// <summary>
    /// How long a prompt written to a session's inbox may stay unseen in the transcript before it is given up and left to the hooks. Longer than the
    /// channel's wait: a session in a long tool call reads the message only at its next tool round, and a held message waits for a dialog.
    /// </summary>
    public TimeSpan InboxConfirmWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long the daemon waits to connect to a session's socket; a socket that does not answer is a session that is gone.</summary>
    public TimeSpan InboxConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the daemon re-reads the saved push settings, so `aipod install --push on` needs no daemon restart.</summary>
    public TimeSpan PushSettingsEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True when push is on and goes through the inbox socket (the daemon's job).</summary>
    public bool InboxPushEnabled => PushEnabled && PushTransport == PushTransports.Inbox;

    /// <summary>True when push goes through the inbox and the inbox gets the one-line notice.</summary>
    public bool InboxShortEnabled => InboxPushEnabled && InboxFormat == InboxFormats.Brief;

    /// <summary>True when push is on and goes through a channel (the MCP process's job).</summary>
    public bool ChannelPushEnabled => PushEnabled && PushTransport == PushTransports.Channel;
}
