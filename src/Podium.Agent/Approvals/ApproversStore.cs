using System.Text.Json;
using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Approvals;

/// <summary>One owner key this machine's admin pinned: the fingerprint (the key's id), a name for people, and the public key (SubjectPublicKeyInfo, base64).</summary>
public sealed record Approver(string Id, string Name, string PublicKey);

/// <summary>
/// The signed-approval policy of this machine (ADR-0010 §4): whether it requires an owner's signature on every run, and the keys it trusts. Off by default.
/// </summary>
public sealed record ApproversPolicy(bool Required, IReadOnlyList<Approver> Keys)
{
    public static readonly ApproversPolicy Off = new(false, []);
}

/// <summary>
/// <c>approvers.json</c>: next to the exec policy of a service agent (admin-owned, never writable by the account that obeys it) and in the home of an interactive
/// one. A missing file is "not required". A file that is there but cannot be trusted (not admin-owned in service mode, unreadable, a link, malformed) is "required
/// with no key", so nothing runs: a broken file never opens the door.
/// </summary>
public static class ApproversStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private sealed record FileShape(bool? Required, List<Approver>? Keys);

    public static ApproversPolicy Load(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var path = config.ApproversPath;
        try
        {
            if (!File.Exists(path)) return ApproversPolicy.Off;
            if (new FileInfo(path).LinkTarget is not null || (config.ServiceMode && !ExecPolicyLoader.IsAdminOwned(path))) return Distrusted;
            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
            if (file is null) return Distrusted;
            var keys = (file.Keys ?? []).Where(k => k is { Id.Length: > 0, PublicKey.Length: > 0 } && Check(k)).ToList();
            return new ApproversPolicy(file.Required == true, keys);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Distrusted;
        }
    }

    private static readonly ApproversPolicy Distrusted = new(true, []);

    /// <summary>A pinned key counts only when its fingerprint is the SHA-256 of the key it carries, so an edited file cannot rename one key into another.</summary>
    private static bool Check(Approver key) =>
        ApprovalSigning.FromBase64(key.PublicKey) is { } spki && ApprovalSigning.IsValidPublicKey(spki) && ApprovalSigning.Fingerprint(spki) == key.Id;

    /// <summary>Pins a key; returns its fingerprint. Throws <see cref="ArgumentException"/> for a key that is not a P-256 public key.</summary>
    public static string Add(AgentConfig config, string publicKeyBase64, string? name)
    {
        var spki = ApprovalSigning.FromBase64(publicKeyBase64);
        if (spki is null || !ApprovalSigning.IsValidPublicKey(spki)) throw new ArgumentException("not an ECDSA P-256 public key (SubjectPublicKeyInfo, base64)", nameof(publicKeyBase64));
        var id = ApprovalSigning.Fingerprint(spki);
        var current = LoadForEdit(config);
        var keys = current.Keys.Where(k => k.Id != id).ToList();
        keys.Add(new Approver(id, Clean(name) ?? "key", Convert.ToBase64String(spki)));
        Save(config, new ApproversPolicy(current.Required, keys));
        return id;
    }

    /// <summary>Removes a pinned key; false when there was none with that fingerprint.</summary>
    public static bool Remove(AgentConfig config, string fingerprint)
    {
        var current = LoadForEdit(config);
        var keys = current.Keys.Where(k => !string.Equals(k.Id, fingerprint, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count == current.Keys.Count) return false;
        Save(config, new ApproversPolicy(current.Required, keys));
        return true;
    }

    public static void SetRequired(AgentConfig config, bool required) => Save(config, LoadForEdit(config) with { Required = required });

    /// <summary>The file as an admin edits it: what is in it, ignoring the ownership check (the admin runs this verb), or an empty policy.</summary>
    private static ApproversPolicy LoadForEdit(AgentConfig config)
    {
        try
        {
            if (!File.Exists(config.ApproversPath)) return ApproversPolicy.Off;
            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(config.ApproversPath), Json);
            return new ApproversPolicy(file?.Required == true, (file?.Keys ?? []).Where(k => k is { Id.Length: > 0, PublicKey.Length: > 0 } && Check(k)).ToList());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return ApproversPolicy.Off;
        }
    }

    private static void Save(AgentConfig config, ApproversPolicy policy)
    {
        var path = config.ApproversPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.Delete(temp);
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileShape(policy.Required, [.. policy.Keys]), Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, FileMode);
        File.Move(temp, path, overwrite: true);
    }

    private static string? Clean(string? name) => name is { Length: > 0 } n ? new string([.. n.Where(c => !char.IsControl(c)).Take(64)]) : null;

    /// <summary>One line for `aipod status`: the mode and how many keys, never a key.</summary>
    public static string Describe(ApproversPolicy policy) =>
        Strings.T(policy.Required ? Msg.ApRequired : Msg.ApOff, policy.Keys.Count);
}
