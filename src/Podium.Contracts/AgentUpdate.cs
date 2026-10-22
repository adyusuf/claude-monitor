using System.Security.Cryptography;
using System.Text;

namespace Podium.Contracts;

// The agent's self-update (ADR-0004). Additive like everything here (global #4).

/// <summary>How far an agent may go on its own: look only, or look and install. Anything unknown counts as off (fail-closed).</summary>
public static class UpdateModes
{
    public const string Off = "off";
    public const string Check = "check";
    public const string On = "on";
    public static readonly IReadOnlyList<string> All = [Off, Check, On];

    public static bool IsValid(string? mode) => mode is Off or Check or On;

    private static int Rank(string? mode) => mode switch { On => 2, Check => 1, _ => 0 };

    /// <summary>The more restrictive of two modes: an update happens only when the machine and the workspace both allow it.</summary>
    public static string Lower(string? a, string? b) => Rank(a) <= Rank(b) ? Normalize(a) : Normalize(b);

    public static string Normalize(string? mode) => mode switch { On => On, Check => Check, _ => Off };
}

/// <summary>
/// What GET /api/agent/latest answers: the newest build of the agent's channel for its OS and CPU. The signature is made
/// offline over <see cref="UpdateManifest.Payload"/> (channel, version, OS, CPU, SHA-256 of the download, minimum
/// supported version); the server only relays it and cannot forge one.
/// </summary>
public sealed record UpdateOffer(string Version, string Url, string Sha256, string Signature, string MinSupported, string Channel);

/// <summary>The one meaning of "a version" for updates: x.y.z, numbers only. The server and the agent both use it.</summary>
public static class UpdateVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var parts = (text ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Length > 9 || !p.All(char.IsAsciiDigit))) return false;
        version = new Version(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }
}

/// <summary>The folder (inside the web root, served at /downloads) that holds the agent builds, SHA256SUMS and manifest.json.</summary>
public static class UpdatePaths
{
    public const string DownloadsFolder = "downloads";
}

/// <summary>
/// Where the agent builds may be fetched from when they are not on the API's own server (ADR-0004 amendment: a download host such as a
/// public object-storage domain). The API checks the configured base, the agent checks every offered address with the same rules. The
/// address is not the trust anchor (the signed manifest is: the agent verifies the SHA-256 and the signature), these rules only keep an
/// offer from pointing the agent at a private or odd destination.
/// </summary>
public static class UpdateUrls
{
    /// <summary>A plain file name, as the manifest and the checksum list use: it becomes the last part of an address.</summary>
    public static bool IsPlainFile(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_') && !name.StartsWith('.');

    /// <summary>The download host's base address (AIPOD_DOWNLOADS_URL): https, a DNS name, no credentials, query or fragment. The trailing slash is dropped.</summary>
    public static bool TryParseBase(string? value, out string baseUrl)
    {
        baseUrl = "";
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !IsPublicHttps(uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>An offered file address outside the agent's own server: https on the default port, a DNS name, no credentials, query or fragment, ending in a plain file name.</summary>
    public static bool IsExternalFile(Uri url) =>
        IsPublicHttps(url) && url.Query.Length == 0 && url.Fragment.Length == 0 && IsPlainFile(url.Segments[^1]);

    private static bool IsPublicHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns
        && uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The signed statement about one build, and its check.</summary>
public static class UpdateManifest
{
    public const string Format = "aipod-update/1";

    /// <summary>
    /// The exact text that is signed. A fixed field order, one field per line: no field may hold a line break, or two
    /// different manifests could produce the same text. scripts/sign_manifest.py builds the same text; a known-answer
    /// test keeps the two in step.
    /// </summary>
    public static string Payload(string channel, string version, string os, string arch, string sha256, string minSupported)
    {
        string[] values = [channel, version, os, arch, sha256, minSupported];
        if (values.Any(v => string.IsNullOrEmpty(v) || v.Contains('\n') || v.Contains('\r')))
        {
            throw new ArgumentException("a manifest field is empty or holds a line break");
        }

        return $"{Format}\nchannel={channel}\nversion={version}\nos={os}\narch={arch}\nsha256={sha256}\nminSupported={minSupported}\n";
    }

    /// <summary>
    /// True only when <paramref name="signature"/> (base64, DER) is a valid ECDSA P-256 / SHA-256 signature of
    /// <paramref name="payload"/> under <paramref name="publicKey"/> (base64 SubjectPublicKeyInfo). A malformed key or
    /// signature is false, never an exception: the caller refuses the update either way.
    /// </summary>
    public static bool Verify(string publicKey, string payload, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Signs <paramref name="payload"/> in the format <see cref="Verify"/> reads (tests; releases are signed by scripts/sign_manifest.py).</summary>
    public static string Sign(ECDsa key, string payload)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }
}
