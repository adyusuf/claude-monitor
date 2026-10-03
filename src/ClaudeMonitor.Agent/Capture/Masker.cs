using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>
/// Masks known secret shapes in every string of a payload before it leaves the hook (ADR-0002: on by default,
/// switchable per workspace). It is a safety net for the common shapes, not a guarantee: a secret in an unknown
/// format passes. Each match becomes "[masked:kind]", so the event still shows that something was there.
/// </summary>
public static partial class Masker
{
    private static readonly (string Kind, Regex Pattern)[] Rules =
    [
        ("private_key", PrivateKey()),
        ("aws_key", AwsKey()),
        ("github_token", GitHubToken()),
        ("anthropic_key", AnthropicKey()),
        ("openai_key", OpenAiKey()),
        ("slack_token", SlackToken()),
        ("google_key", GoogleKey()),
        ("jwt", Jwt()),
        ("connection_password", ConnectionPassword()),
        ("assigned_secret", AssignedSecret()),
    ];

    public static JsonNode? Mask(JsonNode? node) => node switch
    {
        JsonObject o => MaskObject(o),
        JsonArray a => MaskArray(a),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(MaskText(s)),
        _ => node,
    };

    public static string MaskText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (kind, pattern) in Rules)
        {
            text = pattern.Replace(text, m => m.Groups["secret"].Success
                ? m.Value.Replace(m.Groups["secret"].Value, $"[masked:{kind}]", StringComparison.Ordinal)
                : $"[masked:{kind}]");
        }

        return text;
    }

    private static JsonObject MaskObject(JsonObject o)
    {
        foreach (var key in o.Select(p => p.Key).ToList())
        {
            o[key] = Mask(o[key]?.DeepClone());
        }

        return o;
    }

    private static JsonArray MaskArray(JsonArray a)
    {
        for (var i = 0; i < a.Count; i++)
        {
            a[i] = Mask(a[i]?.DeepClone());
        }

        return a;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"\bsk-(proj-)?[A-Za-z0-9_\-]{20,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}\b")]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\b(password|pwd)\s*=\s*(?<secret>(?!\[masked:)[^;""'\s]{4,})")]
    private static partial Regex ConnectionPassword();

    [GeneratedRegex(@"(?i)\b(secret|token|api[_\-]?key|passwd|password|client[_\-]?secret|access[_\-]?key)[""']?\s*[:=]\s*[""']?(?<secret>(?!\[masked:)[^\s""',;]{8,})")]
    private static partial Regex AssignedSecret();
}
