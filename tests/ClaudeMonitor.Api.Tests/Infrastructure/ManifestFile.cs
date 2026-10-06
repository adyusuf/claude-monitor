using System.Text.Json;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>
/// The signed manifest in the test web root's downloads folder (manifest.json is the update tests' alone; disposing removes it).
/// Every write gets a modification time no earlier write shares, because the server's catalog caches by it.
/// </summary>
public sealed class ManifestFile : IDisposable
{
    public const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public const string MacZip = "cm-agent-macos-arm64.zip";
    private static long stamps;

    private readonly string path;

    public ManifestFile(ApiFactory api)
    {
        var folder = Path.Combine(api.WebRoot, "downloads");
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "manifest.json");
        File.Delete(path);
    }

    public void Dispose() => File.Delete(path);

    public void Delete() => File.Delete(path);

    public static Dictionary<string, string?> Entry(string version, string os = "macos", string arch = "arm64", string file = MacZip,
        string sha = Sha, string channel = "test", string minSupported = "0.2.0", string signature = "AAAA") => new()
    {
        ["channel"] = channel, ["version"] = version, ["os"] = os, ["arch"] = arch, ["file"] = file, ["sha256"] = sha,
        ["minSupported"] = minSupported, ["signature"] = signature,
    };

    public static string Json(params Dictionary<string, string?>[] entries) =>
        JsonSerializer.Serialize(new { format = UpdateManifest.Format, entries }, TestUser.Json);

    public void Publish(string content, DateTime? stamp = null)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, stamp ?? new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Interlocked.Increment(ref stamps)));
    }

    public void Publish(params Dictionary<string, string?>[] entries) => Publish(Json(entries));
}
