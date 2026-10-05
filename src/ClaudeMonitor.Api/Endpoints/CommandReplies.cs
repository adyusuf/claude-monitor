using System.Globalization;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>Claude's answer to a command: the event it is in, and the start of its text.</summary>
public sealed record CommandReply(long EventId, string Text, bool More);

/// <summary>
/// Pairs each applied prompt with Claude's first assistant message that has text, written after the command was
/// applied. This is a VIEW over the recorded events: no command status is stored or changed for it. A reply is judged
/// by the time the transcript line itself carries (the agent stamps an event with the moment it READ the line, which
/// can be later than the line was written), and by the event's own time when the line has none.
/// </summary>
public static class CommandReplies
{
    /// <summary>The events' time is the agent's clock and AppliedAt the API's: a minute of look-back covers any skew.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(1);

    private sealed record Candidate(long Id, DateTimeOffset OccurredAt, string? LineAt, string Text);

    public static async Task<IReadOnlyDictionary<Guid, CommandReply>> FindAsync(MonitorDb db, Guid sessionId,
        IReadOnlyCollection<CommandRow> commands, ApiConfig config, CancellationToken ct)
    {
        var asked = commands.Where(c => c.Kind == CommandKinds.Prompt && c.Status == CommandStatuses.Applied && c.AppliedAt is not null).ToList();
        if (asked.Count == 0) return new Dictionary<Guid, CommandReply>();
        var since = asked.Min(c => c.AppliedAt!.Value).ToUniversalTime() - Skew;
        var preview = config.ReplyPreviewChars + 1; // one more than shown tells whether there is more
        var kind = EventKinds.Transcript;

        // Only the first text block's start leaves the database; the rest of an event stays where it is.
        var found = await db.Database.SqlQuery<Candidate>($"""
            SELECT e.id AS "Id", e.occurred_at AS "OccurredAt", e.payload->>'timestamp' AS "LineAt", left(t.text, {preview}) AS "Text"
            FROM session_events e
            CROSS JOIN LATERAL (
                SELECT jsonb_path_query_first(e.payload, '$.message.content[*] ? (@.type == "text").text') #>> ARRAY[]::text[] AS text
            ) t
            WHERE e.session_id = {sessionId} AND e.kind = {kind} AND e.occurred_at >= {since}
              AND e.payload->>'type' = 'assistant' AND btrim(coalesce(t.text, '')) <> ''
            ORDER BY e.occurred_at, e.id
            LIMIT {config.ReplyScanMax}
            """).ToListAsync(ct);

        var replies = new Dictionary<Guid, CommandReply>();
        foreach (var command in asked)
        {
            var reply = found.FirstOrDefault(c => WrittenAt(c) > command.AppliedAt!.Value);
            if (reply is not null) replies[command.Id] = Shorten(reply, config.ReplyPreviewChars);
        }

        return replies;
    }

    private static DateTimeOffset WrittenAt(Candidate c) =>
        DateTimeOffset.TryParse(c.LineAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : c.OccurredAt;

    private static CommandReply Shorten(Candidate c, int max)
    {
        if (c.Text.Length <= max) return new CommandReply(c.Id, c.Text, false);
        var cut = char.IsHighSurrogate(c.Text[max - 1]) ? max - 1 : max; // never half a character
        return new CommandReply(c.Id, c.Text[..cut], true);
    }
}
