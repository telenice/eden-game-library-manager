using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Infrastructure.Persistence;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Security;

/// <summary>Picks the secure password store for the current OS.</summary>
public static class PlatformCredentialStore
{
    public static ICredentialStore Create(AppPaths paths, ILoggerFactory loggerFactory)
    {
        if (OperatingSystem.IsWindows()) return new DpapiCredentialStore(paths, loggerFactory.CreateLogger<DpapiCredentialStore>());
        if (OperatingSystem.IsMacOS()) return new MacKeychainCredentialStore(loggerFactory.CreateLogger<MacKeychainCredentialStore>());
        if (OperatingSystem.IsLinux()) return new SecretToolCredentialStore(loggerFactory.CreateLogger<SecretToolCredentialStore>());
        return new SessionCredentialStore();
    }
}

/// <summary>
/// Shared behaviour: each profile's password is held in memory for the session and only written to
/// the OS secret store when the user asks to remember it. Nothing is ever stored in plaintext.
/// Version 1.0.0 stored a single password; it is read as the password of the migrated profile.
/// </summary>
public abstract class CredentialStoreBase : ICredentialStore
{
    protected const string ServiceName = "EdenGameLibraryManager";
    protected const string LegacyAccountName = "aerofoil";

    private readonly object _lock = new();
    private readonly Dictionary<string, string?> _session = new(StringComparer.Ordinal);

    public abstract bool SupportsPersistence { get; }

    public string? GetPassword(string profileId)
    {
        lock (_lock)
        {
            if (_session.TryGetValue(profileId, out var cached)) return cached;
            string? loaded = null;
            if (SupportsPersistence)
            {
                loaded = SafeRun(() => Load(profileId));
                if (loaded is null && profileId == SettingsService.MigratedProfileId)
                {
                    loaded = SafeRun(LoadLegacy);
                    if (loaded is not null)
                    {
                        SafeRun(() => { Store(profileId, loaded); RemoveLegacy(); return (string?)null; });
                    }
                }
            }
            _session[profileId] = loaded;
            return loaded;
        }
    }

    public void SetPassword(string profileId, string password, bool persist)
    {
        lock (_lock)
        {
            _session[profileId] = password;
            if (!SupportsPersistence) return;
            if (persist) Store(profileId, password);
            else Remove(profileId);
        }
    }

    public void Clear(string profileId)
    {
        lock (_lock)
        {
            _session.Remove(profileId);
            if (SupportsPersistence) SafeRun(() => { Remove(profileId); return (string?)null; });
        }
    }

    protected abstract string? Load(string profileId);
    protected abstract void Store(string profileId, string password);
    protected abstract void Remove(string profileId);
    protected abstract string? LoadLegacy();
    protected abstract void RemoveLegacy();

    private static string? SafeRun(Func<string?> action)
    {
        try
        {
            return action();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Fallback when no OS secret store is available: passwords last for this session only.</summary>
public sealed class SessionCredentialStore : CredentialStoreBase
{
    public override bool SupportsPersistence => false;
    protected override string? Load(string profileId) => null;
    protected override void Store(string profileId, string password) { }
    protected override void Remove(string profileId) { }
    protected override string? LoadLegacy() => null;
    protected override void RemoveLegacy() { }
}

/// <summary>Windows: each password encrypted with DPAPI (current user scope) in credentials-{profile}.dat.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore(AppPaths paths, ILogger<DpapiCredentialStore> logger) : CredentialStoreBase
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EdenGameLibraryManager.credentials.v1");

    public override bool SupportsPersistence => true;

    protected override string? Load(string profileId) => Read(paths.CredentialsFileFor(profileId));
    protected override string? LoadLegacy() => Read(paths.LegacyCredentialsFile);
    protected override void Remove(string profileId) => Delete(paths.CredentialsFileFor(profileId));
    protected override void RemoveLegacy() => Delete(paths.LegacyCredentialsFile);

    protected override void Store(string profileId, string password)
    {
        var file = paths.CredentialsFileFor(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, file, overwrite: true);
    }

    private string? Read(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            logger.LogWarning("Saved credentials could not be decrypted (different Windows user?); they were discarded");
            Delete(file);
            return null;
        }
    }

    private static void Delete(string file)
    {
        if (File.Exists(file)) File.Delete(file);
    }
}

/// <summary>macOS: one generic password item per profile in the user's login Keychain (Security.framework).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore(ILogger<MacKeychainCredentialStore> logger) : CredentialStoreBase
{
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    private static readonly byte[] Service = Encoding.UTF8.GetBytes(ServiceName);

    public override bool SupportsPersistence => true;

    protected override string? Load(string profileId) => Find(Account(profileId));
    protected override string? LoadLegacy() => Find(Encoding.UTF8.GetBytes(LegacyAccountName));
    protected override void Remove(string profileId) => Delete(Account(profileId));
    protected override void RemoveLegacy() => Delete(Encoding.UTF8.GetBytes(LegacyAccountName));

    protected override void Store(string profileId, string password)
    {
        var account = Account(profileId);
        var secret = Encoding.UTF8.GetBytes(password);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, out var data, out var item);
        if (status == 0)
        {
            try
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret);
            }
            finally
            {
                CFRelease(item);
            }
        }
        else
        {
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                (uint)secret.Length, secret, out item);
            if (item != IntPtr.Zero) CFRelease(item);
        }
        if (status != 0) logger.LogWarning("Could not save the password to the Keychain (status {Status})", status);
    }

    private static byte[] Account(string profileId) => Encoding.UTF8.GetBytes("profile:" + profileId);

    private string? Find(byte[] account)
    {
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return null;
        if (status != 0)
        {
            logger.LogWarning("Keychain lookup failed with status {Status}", status);
            return null;
        }
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    private static void Delete(byte[] account)
    {
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, out var data, out var item);
        if (status != 0) return;
        try
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            SecKeychainItemDelete(item);
        }
        finally
        {
            CFRelease(item);
        }
    }

    [DllImport(SecurityLib)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(SecurityLib)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);
}

/// <summary>
/// Linux: the freedesktop Secret Service (GNOME Keyring, KWallet) through libsecret's
/// <c>secret-tool</c>. The secret is passed on stdin, never on the command line.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SecretToolCredentialStore(ILogger<SecretToolCredentialStore> logger) : CredentialStoreBase
{
    private readonly Lazy<string?> _tool = new(FindSecretTool);

    public override bool SupportsPersistence => _tool.Value is not null;

    protected override string? Load(string profileId) => Lookup("profile:" + profileId);
    protected override string? LoadLegacy() => Lookup(LegacyAccountName);
    protected override void Remove(string profileId) => Run(["clear", .. Attributes("profile:" + profileId)], null);
    protected override void RemoveLegacy() => Run(["clear", .. Attributes(LegacyAccountName)], null);

    protected override void Store(string profileId, string password)
    {
        var (exit, _) = Run(["store", "--label=Eden Game Library Manager (AeroFoil)", .. Attributes("profile:" + profileId)], password);
        if (exit != 0) logger.LogWarning("secret-tool could not store the password (exit code {Exit})", exit);
    }

    private static string[] Attributes(string account) => ["application", ServiceName, "account", account];

    private string? Lookup(string account)
    {
        var (exit, output) = Run(["lookup", .. Attributes(account)], null);
        return exit == 0 && output.Length > 0 ? output : null;
    }

    private (int ExitCode, string Output) Run(string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(_tool.Value!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi)!;
            if (stdin is not null) process.StandardInput.Write(stdin);
            process.StandardInput.Close();
            // Read in the background: a blocking read would wait past the limit for a prompt nobody answers.
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            // A keyring unlock prompt may appear, so allow time for the user.
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill();
                return (-1, string.Empty);
            }
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "secret-tool could not be started");
            return (-1, string.Empty);
        }
    }

    private static string? FindSecretTool()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "secret-tool");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
