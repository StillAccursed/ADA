using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using ADA.Core.Secrets;

namespace ADA.Platform.Secrets;

/// <summary>
/// Windows Credential Manager. Entries are generic credentials named <c>ADA/{key}</c>, encrypted
/// by Windows for the signed-in user and kept on this PC only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    // Windows' limit for a credential's secret: CRED_MAX_CREDENTIAL_BLOB_SIZE.
    private const int MAX_SECRET_BYTES = 5 * 512;

    public Task SaveAsync(string key, string label, string secret, CancellationToken ct = default) => Task.Run(() =>
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MAX_SECRET_BYTES)
        {
            throw new ArgumentException("The password is too long for Windows Credential Manager.", nameof(secret));
        }

        var target = Marshal.StringToCoTaskMemUni(Target(key));
        var comment = Marshal.StringToCoTaskMemUni(label);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        // Passed by pointer: a struct argument would need runtime marshalling disabled for the whole assembly.
        var credential = Marshal.AllocCoTaskMem(Marshal.SizeOf<Credential>());
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.StructureToPtr(new Credential
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            }, credential, fDeleteOld: false);
            if (!CredWriteW(credential, 0))
            {
                throw Failed("save");
            }
        }
        finally
        {
            // Clear the copy of the secret before freeing it.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(credential);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(comment);
            Marshal.FreeCoTaskMem(target);
        }
    }, ct);

    public Task<string?> LoadAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!CredReadW(Target(key), CRED_TYPE_GENERIC, 0, out var found))
        {
            if (Marshal.GetLastPInvokeError() == ERROR_NOT_FOUND)
            {
                return null;
            }
            throw Failed("read");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(found);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(found);
        }
    }, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!CredDeleteW(Target(key), CRED_TYPE_GENERIC, 0) && Marshal.GetLastPInvokeError() != ERROR_NOT_FOUND)
        {
            throw Failed("delete");
        }
    }, ct);

    private static string Target(string key) => $"{SecretStores.SERVICE_NAME}/{key}";

    private static SecretStoreUnavailableException Failed(string action) =>
        new($"Windows Credential Manager couldn't {action} the password (error {Marshal.GetLastPInvokeError()}).");

    /// <summary>The Win32 <c>CREDENTIALW</c> structure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(IntPtr credential, int flags);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);
}
