namespace ClaudeMonitor.Agent.Config;

public static class PushScopes
{
    public const string Session = "session";
    public const string Machine = "machine";
}

/// <summary>How a prompt from the web reaches a session when push is on: a Claude Code channel (ADR-0003) or the session's inbox socket (ADR-0007).</summary>
public static class PushTransports
{
    public const string Inbox = "inbox";
    public const string Channel = "channel";

    /// <summary>
    /// The transport a value names. Only "inbox" is the inbox: "on", "channel" and a saved setting with no transport (made before the inbox existed)
    /// all keep meaning the channel, so an upgrade changes nothing for anyone who already had push on.
    /// </summary>
    public static string Normalize(string? value) => value == Inbox ? Inbox : Channel;

    /// <summary>True for the values that switch push on: on, inbox, channel.</summary>
    public static bool Enables(string? value) => value is "on" or Inbox or Channel;
}

/// <summary>A secret that cannot reach a log by accident: its text form, which a record's generated ToString and every interpolation use, hides it.</summary>
public sealed record SecretText(string Value)
{
    public static implicit operator SecretText?(string? value) => value is null ? null : new SecretText(value);

    public override string ToString() => "[secret]";
}
