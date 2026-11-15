using System.Security.Cryptography;
using System.Text;
using Podium.Agent.Access;

namespace Podium.Agent.Tests;

/// <summary>
/// The SHA-256 an access layer pins an executable to (ADR-0020 §4) is the file's as it is now: a program swapped in place for another of the same size,
/// its modification time put back, must not keep the old hash.
/// </summary>
public sealed class ExecutableHashesTests : IDisposable
{
    private readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ap-hash-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void A_file_swapped_in_place_for_one_of_the_same_size_with_its_modification_time_restored_gets_its_new_hash()
    {
        var exe = Path.Combine(dir, "tool");
        File.WriteAllText(exe, "allowed program");
        var written = File.GetLastWriteTimeUtc(exe);
        Assert.Equal(Sha("allowed program"), ExecutableHashes.Of(exe));

        File.WriteAllText(exe, "swapped program");
        File.SetLastWriteTimeUtc(exe, written);

        Assert.Equal(Sha("swapped program"), ExecutableHashes.Of(exe));
    }

    [Fact]
    public void A_missing_file_has_no_hash_so_no_listed_hash_matches_it()
    {
        Assert.Null(ExecutableHashes.Of(Path.Combine(dir, "missing")));
    }
}
