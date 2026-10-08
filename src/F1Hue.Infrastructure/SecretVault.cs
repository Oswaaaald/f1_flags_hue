using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed record BridgeCredentials(string Ip, string ApplicationKey, string? ClientKey = null, string? CertificatePin = null, string? BridgeId = null, string? Name = null);
public interface IBridgeVault
{
    BridgeCredentials? Read(); void Save(BridgeCredentials credentials); void Clear();
}

/// <summary>Encrypted at rest. The key uses macOS Keychain or Windows DPAPI; Linux keeps it in a private directory.</summary>
public sealed class SecretVault(string directory) : IBridgeVault
{
    private readonly object _gate = new();
    private readonly string _file = Path.Combine(directory, "bridge.enc");
    private BridgeCredentials? _cached;
    private bool _loaded;
    private byte[] Key()
    {
        if (OperatingSystem.IsMacOS())
            return MacKeychain.Key(Path.GetFullPath(directory));
        var path = Path.Combine(directory, "vault.key");
        if (!File.Exists(path))
        {
            var generated = RandomNumberGenerator.GetBytes(32);
            if (OperatingSystem.IsWindows())
                generated = ProtectedData.Protect(generated, null, DataProtectionScope.CurrentUser);
            PrivateFiles.Write(path, Convert.ToBase64String(generated));
        }
        var stored = Convert.FromBase64String(File.ReadAllText(path));
        return OperatingSystem.IsWindows() ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser) : stored;
    }
    public BridgeCredentials? Read()
    {
        lock (_gate)
        {
            if (_loaded)
                return _cached;
            if (!File.Exists(_file))
            {
                _loaded = true;
                return null;
            }
            var bytes = Convert.FromBase64String(File.ReadAllText(_file));
            if (bytes.Length < 28)
                throw new InvalidOperationException("Le coffre Hue est endommagé.");
            var plain = new byte[bytes.Length - 28];
            using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            try
            {
                _cached = JsonSerializer.Deserialize<BridgeCredentials>(plain, JsonDefaults.Options);
                _loaded = true;
                return _cached;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Save(BridgeCredentials credentials)
    {
        lock (_gate)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonDefaults.Options);
            try
            {
                var encrypted = new byte[plain.Length + 28];
                RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
                using var aes = new AesGcm(Key(), 16);
                aes.Encrypt(encrypted.AsSpan(0, 12), plain, encrypted.AsSpan(28), encrypted.AsSpan(12, 16));
                PrivateFiles.Write(_file, Convert.ToBase64String(encrypted));
                _cached = credentials;
                _loaded = true;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_file))
                File.Delete(_file);
            _cached = null;
            _loaded = true;
        }
    }
}

internal static class MacKeychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint length, out IntPtr data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, byte[] data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr item);
    public static byte[] Key(string accountName)
    {
        var service = Encoding.UTF8.GetBytes("F1Hue.Vault");
        var account = Encoding.UTF8.GetBytes(accountName);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
        if (status == 0)
        {
            try
            {
                var result = new byte[length];
                Marshal.Copy(data, result, 0, result.Length);
                return result;
            }
            finally { SecKeychainItemFreeContent(IntPtr.Zero, data); if (item != IntPtr.Zero) CFRelease(item); }
        }
        if (status != -25300)
            throw new InvalidOperationException("Déverrouille le trousseau macOS pour accéder au pont Hue.");
        var key = RandomNumberGenerator.GetBytes(32);
        status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)key.Length, key, out item);
        if (item != IntPtr.Zero)
            CFRelease(item);
        if (status != 0)
            throw new InvalidOperationException("Impossible d’enregistrer la clé Hue dans le trousseau macOS.");
        return key;
    }
}
