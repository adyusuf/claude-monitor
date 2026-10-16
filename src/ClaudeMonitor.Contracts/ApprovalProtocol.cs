using System.Security.Cryptography;
using System.Text;

namespace ClaudeMonitor.Contracts;

/// <summary>An owner registers the public half of an approval key the browser made (ADR-0010 §4). PublicKey is the SubjectPublicKeyInfo of an ECDSA P-256 key, base64.</summary>
public sealed record ApprovalKeyCreate(string Name, string PublicKey, string? Code = null);

/// <summary>An approval key as the web lists it. The fingerprint is what a person compares on the machine that pins the key.</summary>
public sealed record ApprovalKeyView(Guid Id, string Name, string Fingerprint, string PublicKey, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);

/// <summary>Why a target in signed-approval mode refused a run, as stable codes (ADR-0010 §4).</summary>
public static class ApprovalErrors
{
    /// <summary>The run carries no signature, or was approved by a grant or a job (those carry none).</summary>
    public const string Required = "approval_required";

    /// <summary>The signature does not verify for this run, or it is out of time.</summary>
    public const string Invalid = "approval_invalid";

    /// <summary>The signature names a key this machine did not pin.</summary>
    public const string KeyUnknown = "approval_key_unknown";

    /// <summary>The machine requires signed approvals and no key is pinned (or the file cannot be trusted), so nothing can run.</summary>
    public const string KeyMissing = "approval_key_missing";
}

/// <summary>
/// What an owner's browser key signs and how it is checked (ADR-0010 §4): ECDSA over P-256 with SHA-256, the signature in the IEEE P1363 form WebCrypto makes. One definition
/// for the API (which checks what it stores) and the target agent (which checks before it runs anything).
/// </summary>
public static class ApprovalSigning
{
    public const string Tag = "cm-approval-v1";
    public const string KindRun = "run";

    /// <summary>The exact bytes signed: a tag, the kind, the run's id, the target agent, the run's hash and the signed expiry in whole Unix seconds, one per line.</summary>
    public static byte[] Payload(string kind, Guid id, Guid targetAgentId, string hash, DateTimeOffset notAfter) =>
        Encoding.UTF8.GetBytes($"{Tag}\n{kind}\n{id:D}\n{targetAgentId:D}\n{hash}\n{notAfter.ToUnixTimeSeconds()}");

    /// <summary>Lowercase hex SHA-256 of the key's SubjectPublicKeyInfo: what the web shows and a person compares with what the machine shows.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> spki) => Convert.ToHexStringLower(SHA256.HashData(spki));

    /// <summary>True when the bytes are the SubjectPublicKeyInfo of a P-256 key.</summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> spki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            return read == spki.Length && key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="signature"/> (64 bytes, r then s) is a valid signature of <paramref name="payload"/> under the key.</summary>
    public static bool Verify(ReadOnlySpan<byte> spki, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64 || !IsValidPublicKey(spki)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Base64 (standard or URL-safe, padded or not) to bytes; null when it is not base64.</summary>
    public static byte[]? FromBase64(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4096) return null;
        var normal = text.Replace('-', '+').Replace('_', '/');
        normal = normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(normal);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
