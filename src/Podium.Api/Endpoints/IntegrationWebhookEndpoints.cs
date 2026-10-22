using System.Security.Cryptography;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// The address Jira calls (ADR-0015 §6). No session: the signed token is the only credential. The body is never trusted; it only says which issue
/// or sprint to read again, and that is queued. Always answers quickly, and 200 for a repeated delivery.
/// </summary>
public static class IntegrationWebhookEndpoints
{
    public const int BodyMax = 256 * 1024;
    private const string DeliveryHeader = "X-Atlassian-Webhook-Identifier";

    public static void Map(RouteGroupBuilder api) => api.MapPost("/integrations/jira/webhook/{id:guid}", Receive);

    private static async Task<IResult> Receive(Guid id, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, SyncInbox inbox)
    {
        var ct = http.RequestAborted;
        if (!config.JiraAvailable) return Results.NotFound();
        if (!WebhookJwt.Verify(http.Request.Headers.Authorization.ToString(), config.Jira!.ClientSecret, clock.GetUtcNow())) return Results.Unauthorized();
        var connection = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Status == ConnectionStatuses.Active, ct);
        if (connection is null) return Results.NotFound();

        var body = await ReadAsync(http.Request.Body, ct);
        if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var delivery = http.Request.Headers[DeliveryHeader].ToString() is { Length: > 0 and <= 100 } header ? header : Convert.ToHexStringLower(SHA256.HashData(body));
        if (await db.IntegrationWebhookReceipts.AnyAsync(r => r.ConnectionId == id && r.DeliveryId == delivery, ct)) return Results.Ok();
        db.IntegrationWebhookReceipts.Add(new IntegrationWebhookReceipt { ConnectionId = id, DeliveryId = delivery, ReceivedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);

        if (Parse(body, id) is { } request) inbox.Enqueue(request);
        return Results.Ok();
    }

    /// <summary>The queued read for an event, or null for one we do not act on. Only an issue key or a sprint id is taken from the body.</summary>
    internal static SyncRequest? Parse(byte[] body, Guid connectionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var kind = JiraParse.Str(root, "webhookEvent");
            if (kind is not null && kind.StartsWith("jira:issue_", StringComparison.Ordinal) && root.TryGetProperty("issue", out var issue)
                && JiraParse.Str(issue, "key") is { Length: > 0 and <= 64 } key)
            {
                return new SyncRequest(connectionId, SyncTarget.Issue, key);
            }

            if (kind is not null && kind.StartsWith("sprint_", StringComparison.Ordinal) && root.TryGetProperty("sprint", out var sprint)
                && sprint.TryGetProperty("id", out var sid) && sid.ToString() is { Length: > 0 and <= 20 } sprintId && sprintId.All(char.IsAsciiDigit))
            {
                return new SyncRequest(connectionId, SyncTarget.Sprint, sprintId);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > BodyMax) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
