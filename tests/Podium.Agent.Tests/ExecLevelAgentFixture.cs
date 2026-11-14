using System.Security.Cryptography;
using Podium.Agent.Approvals;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// A connected machine in a throw-away home, an owner key that can be pinned on it, and the requests the API would deliver for the exec-level tests
/// (ADR-0021). The clock stands still unless a test moves it.
/// </summary>
public abstract class ExecLevelAgentFixture : IDisposable
{
    protected static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    protected static readonly Guid NewLogin = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    protected TempHome Home { get; } = new();
    protected LocalStore Store { get; }
    protected ManualClock Clock { get; } = new(Now);
    protected AgentLog Log { get; }
    protected Guid Agent { get; } = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    protected ExecLevelAgentFixture()
    {
        Store = new LocalStore(Home.Config.DatabasePath);
        Log = new AgentLog(Home.Config, Clock);
        SaveIdentity(Agent, ExecLevels.Off);
    }

    public void Dispose()
    {
        Store.Dispose();
        owner.Dispose();
        Home.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void SaveIdentity(Guid id, string level) =>
        new Identity(new string('k', 24), "https://aipod.invalid", id, Guid.NewGuid(), ExecLevel: level).Save(Home.Config);

    protected string LevelOnDisk() => Identity.Peek(Home.Config)!.ExecLevel!;

    protected bool Handle(ExecLevelRequest? request, AgentConfig? config = null) => ExecLevelChanges.Handle(config ?? Home.Config, Store, Clock, Log, request);

    protected static ExecLevelRequest Bare(string level, long sequence, TimeSpan? validFor = null) => new(level, sequence, Now + (validFor ?? TimeSpan.FromMinutes(5)));

    /// <summary>Pins the owner key on the machine the way <c>aipod approvers add</c> does.</summary>
    protected void Pin() => ApproversStore.Add(Home.Config, Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()), "laptop");

    /// <summary>A change the owner key signed for this machine (or, with the overrides, for something else), the way the browser signs it.</summary>
    protected ExecLevelRequest OwnerSigned(string level, long sequence, TimeSpan? validFor = null, Guid? forAgent = null, string? signedLevel = null)
    {
        var notAfter = Now + (validFor ?? TimeSpan.FromMinutes(5));
        var bytes = ExecLevelSigning.Payload(forAgent ?? Agent, signedLevel ?? level, sequence, notAfter);
        var signature = owner.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new ExecLevelRequest(level, sequence, notAfter, ApprovalSigning.Fingerprint(owner.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(signature));
    }
}
