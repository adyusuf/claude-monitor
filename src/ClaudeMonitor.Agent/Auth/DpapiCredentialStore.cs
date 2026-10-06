using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>
/// Credentials of the Windows service (ADR-0005): DPAPI in the service account's CurrentUser scope, one
/// <c>cred-&lt;account&gt;.bin</c> file each, written through a temp file and an atomic rename. Never LocalMachine scope,
/// which any account on the machine could decrypt. A read returns null on any failure.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DpapiCredentialStore(string home) : ICredentialStore
{
    private const string FilePrefix = "cred-";
    private const string FileSuffix = ".bin";
    private const string TempSuffix = ".tmp";

    // CRYPTPROTECT_UI_FORBIDDEN only. CRYPTPROTECT_LOCAL_MACHINE (0x4) must never be added.
    private const uint UiForbidden = 0x1;

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(AgentConfig.CredentialService);

    /// <summary>False when the account has no loaded profile (no AppData): DPAPI keys would not be stable, so the service must not start.</summary>
    public static bool ProfileLoaded() => !string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public string? Read(string account)
    {
        if (!IsValidAccount(account)) return null;
        try
        {
            var path = PathOf(account);
            if (!File.Exists(path)) return null;
            var plain = Unprotect(File.ReadAllBytes(path));
            if (plain is null) return null;
            try
            {
                return Utf8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // unreadable is the same as absent: the caller asks the user to log in again
            return null;
        }
    }

    public void Write(string account, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!IsValidAccount(account)) throw new ArgumentException("not a valid credential name", nameof(account));
        var plain = Utf8.GetBytes(secret);
        byte[] sealedBytes;
        try
        {
            sealedBytes = Protect(plain) ?? throw new InvalidOperationException($"CryptProtectData failed ({Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            Array.Clear(plain);
        }

        Directory.CreateDirectory(home);
        var target = PathOf(account);
        var temp = target + TempSuffix;
        var moved = false;
        try
        {
            File.WriteAllBytes(temp, sealedBytes);
            File.Move(temp, target, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved) File.Delete(temp);
        }
    }

    public void Delete(string account)
    {
        if (!IsValidAccount(account)) return;
        File.Delete(PathOf(account));
    }

    private string PathOf(string account) => Path.Combine(home, FilePrefix + account + FileSuffix);

    private static bool IsValidAccount(string account) =>
        !string.IsNullOrEmpty(account) && account.IndexOfAny(['/', '\\', '\0']) < 0 && account != "." && account != "..";

    private static byte[]? Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[]? Unprotect(byte[] sealedBytes) => Transform(sealedBytes, protect: false);

    private static unsafe byte[]? Transform(byte[] input, bool protect)
    {
        fixed (byte* inPtr = input)
        fixed (byte* entropyPtr = Entropy)
        {
            var data = new DataBlob { Size = (uint)input.Length, Data = (IntPtr)inPtr };
            var entropy = new DataBlob { Size = (uint)Entropy.Length, Data = (IntPtr)entropyPtr };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref data, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                // the returned blob may hold the secret: wipe it before giving it back
                new Span<byte>((void*)output.Data, (int)output.Size).Clear();
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
