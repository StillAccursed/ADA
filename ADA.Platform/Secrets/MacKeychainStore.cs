using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ADA.Core.Secrets;

namespace ADA.Platform.Secrets;

/// <summary>
/// The login keychain, through Security.framework's <c>SecItem</c> functions. Entries are generic
/// passwords with service <c>ADA</c> and the key as the account, so Keychain Access lists them as "ADA".
/// </summary>
/// <remarks>
/// The first read by a differently signed build (e.g. after rebuilding a development copy) makes
/// macOS ask the user whether ADA may use the entry. That prompt blocks the calling thread.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacKeychainStore : ISecretStore
{
    private const string SECURITY = "/System/Library/Frameworks/Security.framework/Security";
    private const string CORE_FOUNDATION = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CF_STRING_ENCODING_UTF8 = 0x08000100;
    private const int ERR_SEC_SUCCESS = 0;
    private const int ERR_SEC_DUPLICATE_ITEM = -25299;
    private const int ERR_SEC_ITEM_NOT_FOUND = -25300;

    private static readonly IntPtr SECURITY_LIBRARY = NativeLibrary.Load(SECURITY);
    private static readonly IntPtr CORE_FOUNDATION_LIBRARY = NativeLibrary.Load(CORE_FOUNDATION);

    // The keys and values SecItem dictionaries use are exported constants, not literals.
    private static readonly IntPtr SEC_CLASS = Constant(SECURITY_LIBRARY, "kSecClass");
    private static readonly IntPtr SEC_CLASS_GENERIC_PASSWORD = Constant(SECURITY_LIBRARY, "kSecClassGenericPassword");
    private static readonly IntPtr SEC_ATTR_SERVICE = Constant(SECURITY_LIBRARY, "kSecAttrService");
    private static readonly IntPtr SEC_ATTR_ACCOUNT = Constant(SECURITY_LIBRARY, "kSecAttrAccount");
    private static readonly IntPtr SEC_ATTR_LABEL = Constant(SECURITY_LIBRARY, "kSecAttrLabel");
    private static readonly IntPtr SEC_VALUE_DATA = Constant(SECURITY_LIBRARY, "kSecValueData");
    private static readonly IntPtr SEC_RETURN_DATA = Constant(SECURITY_LIBRARY, "kSecReturnData");
    private static readonly IntPtr CF_BOOLEAN_TRUE = Constant(CORE_FOUNDATION_LIBRARY, "kCFBooleanTrue");

    // Dictionary callbacks are passed by address, not read.
    private static readonly IntPtr DICTIONARY_KEY_CALLBACKS = NativeLibrary.GetExport(CORE_FOUNDATION_LIBRARY, "kCFTypeDictionaryKeyCallBacks");
    private static readonly IntPtr DICTIONARY_VALUE_CALLBACKS = NativeLibrary.GetExport(CORE_FOUNDATION_LIBRARY, "kCFTypeDictionaryValueCallBacks");

    public Task SaveAsync(string key, string label, string secret, CancellationToken ct = default) => Task.Run(() =>
    {
        using var query = Query(key);
        using var data = Data(secret);
        using var labelText = Text(label);
        using var update = Dictionary((SEC_VALUE_DATA, data.Handle), (SEC_ATTR_LABEL, labelText.Handle));
        var status = SecItemUpdate(query.Handle, update.Handle);
        if (status == ERR_SEC_ITEM_NOT_FOUND)
        {
            using var service = Text(SecretStores.SERVICE_NAME);
            using var account = Text(key);
            using var item = Dictionary((SEC_CLASS, SEC_CLASS_GENERIC_PASSWORD), (SEC_ATTR_SERVICE, service.Handle),
                (SEC_ATTR_ACCOUNT, account.Handle), (SEC_ATTR_LABEL, labelText.Handle), (SEC_VALUE_DATA, data.Handle));
            status = SecItemAdd(item.Handle, IntPtr.Zero);
        }
        ThrowIfFailed(status, "save");
    }, ct);

    public Task<string?> LoadAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        using var service = Text(SecretStores.SERVICE_NAME);
        using var account = Text(key);
        using var query = Dictionary((SEC_CLASS, SEC_CLASS_GENERIC_PASSWORD), (SEC_ATTR_SERVICE, service.Handle),
            (SEC_ATTR_ACCOUNT, account.Handle), (SEC_RETURN_DATA, CF_BOOLEAN_TRUE));
        var status = SecItemCopyMatching(query.Handle, out var result);
        if (status == ERR_SEC_ITEM_NOT_FOUND)
        {
            return null;
        }
        ThrowIfFailed(status, "read");

        using var data = new CoreFoundationObject(result);
        var bytes = new byte[CFDataGetLength(data.Handle)];
        Marshal.Copy(CFDataGetBytePtr(data.Handle), bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }, ct);

    public Task DeleteAsync(string key, CancellationToken ct = default) => Task.Run(() =>
    {
        using var query = Query(key);
        var status = SecItemDelete(query.Handle);
        if (status != ERR_SEC_ITEM_NOT_FOUND)
        {
            ThrowIfFailed(status, "delete");
        }
    }, ct);

    /// <summary>Matches ADA's entry for <paramref name="key"/>.</summary>
    private static CoreFoundationObject Query(string key)
    {
        using var service = Text(SecretStores.SERVICE_NAME);
        using var account = Text(key);
        // The dictionary retains its keys and values, so releasing these afterwards is safe.
        return Dictionary((SEC_CLASS, SEC_CLASS_GENERIC_PASSWORD), (SEC_ATTR_SERVICE, service.Handle), (SEC_ATTR_ACCOUNT, account.Handle));
    }

    private static CoreFoundationObject Text(string value) =>
        new(CFStringCreateWithCString(IntPtr.Zero, value, CF_STRING_ENCODING_UTF8));

    private static CoreFoundationObject Data(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new CoreFoundationObject(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));
    }

    private static CoreFoundationObject Dictionary(params (IntPtr Key, IntPtr Value)[] entries) =>
        new(CFDictionaryCreate(IntPtr.Zero, entries.Select(e => e.Key).ToArray(), entries.Select(e => e.Value).ToArray(),
            entries.Length, DICTIONARY_KEY_CALLBACKS, DICTIONARY_VALUE_CALLBACKS));

    private static IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

    /// <summary>Codes other than success mean the keychain refused or is locked; ADA can't save there.</summary>
    private static void ThrowIfFailed(int status, string action)
    {
        if (status != ERR_SEC_SUCCESS)
        {
            throw new SecretStoreUnavailableException($"The keychain couldn't {action} the password (error {status}).");
        }
    }

    /// <summary>Releases a Core Foundation object once, when disposed.</summary>
    private sealed class CoreFoundationObject(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; private set; } = handle;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CFRelease(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    [LibraryImport(CORE_FOUNDATION, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CORE_FOUNDATION)]
    private static partial void CFRelease(IntPtr value);

    [LibraryImport(SECURITY)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SECURITY)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SECURITY)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SECURITY)]
    private static partial int SecItemDelete(IntPtr query);
}
