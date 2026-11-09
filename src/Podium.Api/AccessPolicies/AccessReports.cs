using System.Text.Json;
using Podium.Contracts;

namespace Podium.Api.AccessPolicies;

/// <summary>
/// The access report an agent sends with its profile (ADR-0020 §4): its ceiling and the state of each policy it received. Text from a machine: every
/// list is capped, a path that is empty, too long or holds a hidden character is dropped, a SHA-256 that is not lowercase hex is dropped, and a policy
/// entry with an unknown state is dropped. The stored form is the cleaned report as JSON.
/// </summary>
public static class AccessReports
{
    public const int CeilingMax = 64;
    public const int KeyIdMax = 64;
    public const int ProjectFoldersMax = 10_000;

    public static string? Store(AccessReport? report)
    {
        if (report?.Ceiling is not { } ceiling) return null;
        var clean = new AccessReport(
            new AccessCeilingReport(Paths(ceiling.AllowedFolders), Paths(ceiling.DeniedFolders), Apps(ceiling.Applications), ceiling.Shell),
            (report.Policies ?? []).Where(p => p is not null && AccessPolicyStates.All.Contains(p.State)).Take(AccessPolicyDelivery.PoliciesMax)
                .Select(p => new AccessAppliedReport(p.ProjectId, Math.Max(0, p.Version), p.State, p.IsSigned,
                    p.KeyId is { Length: > 0 and <= KeyIdMax } k && IsPlain(k) ? k : null, Math.Clamp(p.ProjectFolders, 0, ProjectFoldersMax)))
                .ToList());
        return JsonSerializer.Serialize(clean, AccessPolicyStore.Json);
    }

    /// <summary>The stored report, or null when there is none; a stored value the API cannot read is treated as none (the view says "not reported").</summary>
    public static AccessReport? Read(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<AccessReport>(json, AccessPolicyStore.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPlain(string text) => !TextSafety.HasHidden(text) && !text.Any(char.IsControl);

    private static List<string> Paths(IReadOnlyList<string>? paths) =>
        (paths ?? []).Where(p => p is { Length: > 0 and <= AccessPolicyRules.MaxPathLength } && IsPlain(p)).Distinct(StringComparer.Ordinal).Take(CeilingMax).ToList();

    private static List<AccessApp> Apps(IReadOnlyList<AccessApp>? apps) =>
        (apps ?? []).Where(a => a?.Path is { Length: > 0 and <= AccessPolicyRules.MaxPathLength } p && IsPlain(p)).Take(CeilingMax)
            .Select(a => new AccessApp(a.Path, AccessPolicyRules.IsSha256(a.Sha256) ? a.Sha256 : null)).ToList();
}
