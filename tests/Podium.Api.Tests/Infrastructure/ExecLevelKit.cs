using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Podium.Contracts;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>An owner's approval key that signs exec-level changes the way the web does (ADR-0021): P-256, IEEE P1363, over <see cref="ExecLevelSigning.Payload"/>.</summary>
public sealed class ExecKey : IDisposable
{
    private readonly ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string Spki => Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    public string Id => ApprovalSigning.Fingerprint(ecdsa.ExportSubjectPublicKeyInfo());

    public string Sign(Guid agentId, string level, long sequence, DateTimeOffset notAfter) =>
        Convert.ToBase64String(ecdsa.SignData(ExecLevelSigning.Payload(agentId, level, sequence, notAfter), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => ecdsa.Dispose();
}

/// <summary>Setup and reading helpers of the exec-level tests; every call goes through the public API.</summary>
public static class ExecLevelKit
{
    public static async Task<ExecKey> RegisterKeyAsync(TestUser owner)
    {
        var key = new ExecKey();
        (await owner.PostAsync("/api/me/approval-keys", new { name = "laptop", publicKey = key.Spki })).EnsureSuccessStatusCode();
        return key;
    }

    public static Task<HttpResponseMessage> PutAsync(TestUser user, Guid agentId, ExecLevelChange change) =>
        user.SendAsync(HttpMethod.Put, $"/api/agents/{agentId}/exec-level", change);

    public static Task<HttpResponseMessage> GetAsync(TestUser user, Guid agentId) => user.Http.GetAsync($"/api/agents/{agentId}/exec-level");

    /// <summary>The change a browser sends: signed when a key is given, otherwise the bare request (a lowering).</summary>
    public static ExecLevelChange Change(Guid agentId, string level, long sequence, DateTimeOffset notAfter, ExecKey? key = null, string? code = null) =>
        key is null
            ? new ExecLevelChange(level, sequence, notAfter, Code: code)
            : new ExecLevelChange(level, sequence, notAfter, key.Id, key.Sign(agentId, level, sequence, notAfter), code);

    /// <summary>The deadline a browser would sign: this far after the test clock's now.</summary>
    public static DateTimeOffset Deadline(ApiFactory api, int minutes = 5) => api.Clock.GetUtcNow().AddMinutes(minutes);

    public static async Task<JsonElement> ViewAsync(TestUser user, Guid agentId)
    {
        var response = await GetAsync(user, agentId);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>What the machine's agent is told in its settings: the request it must handle, or null.</summary>
    public static async Task<ExecLevelRequest?> DeliveredAsync(TestAgent agent) =>
        (await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.ExecLevelRequest;

    /// <summary>The agent's profile with what it did with the latest change, as the daemon sends it.</summary>
    public static async Task ReportAsync(TestAgent agent, string level, ExecLevelReport report) =>
        (await agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(level, false, null, null, 2, ExecLevelChange: report), TestUser.Json)).EnsureSuccessStatusCode();
}
