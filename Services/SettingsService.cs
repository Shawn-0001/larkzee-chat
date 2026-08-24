using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LarkzeeChat.Models;
using LarkzeeChat.Networking;

namespace LarkzeeChat.Services;

/// <summary>
/// Reads and writes local settings. Password fields are protected with the
/// current Windows user's DPAPI key before they are written to disk.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("LarkzeeChat.settings.v2");

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _settingsPath;
    private readonly bool _allowLegacyIpFallback;

    public SettingsService()
        : this(GetDefaultSettingsPath(), allowLegacyIpFallback: true)
    {
    }

    // Kept injectable for code-level checks without changing the production path.
    public SettingsService(string settingsPath)
        : this(settingsPath, allowLegacyIpFallback: false)
    {
    }

    private SettingsService(string settingsPath, bool allowLegacyIpFallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
        _allowLegacyIpFallback = allowLegacyIpFallback;
        EnsureDirectory();
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        EnsureDirectory();

        if (!File.Exists(_settingsPath))
        {
            return _allowLegacyIpFallback
                ? LoadLegacyRemoteIp()
                : new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            PersistedSettings? persisted = JsonSerializer.Deserialize<PersistedSettings>(json, SerializerOptions);
            if (persisted is null)
            {
                return new AppSettings();
            }

            var settings = new AppSettings
            {
                RemoteIp = persisted.RemoteIp?.Trim() ?? string.Empty
            };

            settings.LocalPassword = UnprotectPassword(persisted.LocalPasswordProtected);
            settings.RemotePassword = UnprotectPassword(persisted.RemotePasswordProtected);
            settings.RemoteConnectionCode = UnprotectConnectionCode(persisted.RemoteConnectionCodeProtected);
            if (ConnectionCodeService.TryDecode(
                    settings.RemoteConnectionCode,
                    out ConnectionCodeInfo connectionCode,
                    out _))
            {
                // A code-only settings file can reconnect after restart. If
                // legacy/manual fields are present, preserve them so adding
                // this optional field cannot unexpectedly change an existing
                // user's explicitly configured peer credential.
                if (string.IsNullOrWhiteSpace(settings.RemoteIp))
                {
                    settings.RemoteIp = connectionCode.Address.ToString();
                }

                if (string.IsNullOrWhiteSpace(settings.RemotePassword))
                {
                    settings.RemotePassword = connectionCode.AuthenticationPassword;
                }
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A malformed or temporarily inaccessible preferences file must
            // not stop startup. A malformed document has no trustworthy IP or
            // secret fields, so fail closed to defaults.
            Debug.WriteLine($"Larkzee Chat settings could not be loaded: {exception.Message}");
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureDirectory();

        try
        {
            var persisted = new PersistedSettings
            {
                RemoteIp = settings.RemoteIp?.Trim() ?? string.Empty,
                LocalPasswordProtected = ProtectPassword(settings.LocalPassword),
                RemotePasswordProtected = ProtectPassword(settings.RemotePassword),
                RemoteConnectionCodeProtected = ProtectConnectionCode(settings.RemoteConnectionCode)
            };

            string json = JsonSerializer.Serialize(persisted, SerializerOptions);
            File.WriteAllText(_settingsPath, json);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or CryptographicException
                                          or PlatformNotSupportedException)
        {
            Debug.WriteLine($"Larkzee Chat settings could not be saved: {exception.Message}");
            return false;
        }
    }

    private static string GetDefaultSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".larkzeeChat",
            "settings.json");
    }

    private static string GetLegacySettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LarkzeeChat",
            "settings.json");
    }

    private static string? ProtectPassword(string? password)
    {
        if (!AuthenticationService.TryValidateManualPassword(password, out string validatedPassword))
        {
            return null;
        }

        byte[] plaintext = StrictUtf8.GetBytes(validatedPassword);
        byte[] protectedBytes = Array.Empty<byte>();
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private static string? ProtectConnectionCode(string? code)
    {
        if (!ConnectionCodeService.TryDecode(
                code,
                out ConnectionCodeInfo connectionCode,
                out _))
        {
            return null;
        }

        byte[] plaintext = StrictUtf8.GetBytes(connectionCode.Code);
        byte[] protectedBytes = Array.Empty<byte>();
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    private static string UnprotectPassword(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        byte[] protectedBytes = Array.Empty<byte>();
        byte[] plaintext = Array.Empty<byte>();
        try
        {
            protectedBytes = Convert.FromBase64String(encoded);
            if (protectedBytes.Length == 0)
            {
                return string.Empty;
            }

            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            string candidate = StrictUtf8.GetString(plaintext);
            return AuthenticationService.TryValidateManualPassword(candidate, out string validatedPassword)
                ? validatedPassword
                : string.Empty;
        }
        catch (Exception exception) when (exception is FormatException
                                          or CryptographicException
                                          or PlatformNotSupportedException
                                          or ArgumentException
                                          or DecoderFallbackException)
        {
            // A bad protected value invalidates only this secret. The caller
            // independently loads the IP and the other password field.
            Debug.WriteLine($"Larkzee Chat protected setting could not be loaded: {exception.Message}");
            return string.Empty;
        }
        finally
        {
            if (protectedBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (plaintext.Length != 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static string UnprotectConnectionCode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return string.Empty;
        }

        byte[] protectedBytes = Array.Empty<byte>();
        byte[] plaintext = Array.Empty<byte>();
        try
        {
            protectedBytes = Convert.FromBase64String(encoded);
            if (protectedBytes.Length == 0)
            {
                return string.Empty;
            }

            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            string candidate = StrictUtf8.GetString(plaintext);
            return ConnectionCodeService.TryDecode(
                    candidate,
                    out ConnectionCodeInfo connectionCode,
                    out _)
                ? connectionCode.Code
                : string.Empty;
        }
        catch (Exception exception) when (exception is FormatException
                                          or CryptographicException
                                          or PlatformNotSupportedException
                                          or ArgumentException
                                          or DecoderFallbackException)
        {
            // A bad protected code invalidates only this field. The peer IP
            // and the two independent password fields remain loadable.
            Debug.WriteLine($"Larkzee Chat protected connection code could not be loaded: {exception.Message}");
            return string.Empty;
        }
        finally
        {
            if (protectedBytes.Length != 0)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (plaintext.Length != 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private AppSettings LoadLegacyRemoteIp()
    {
        string legacyPath = GetLegacySettingsPath();
        if (!File.Exists(legacyPath))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(legacyPath);
            LegacyPersistedSettings? legacy = JsonSerializer.Deserialize<LegacyPersistedSettings>(json, SerializerOptions);
            return new AppSettings
            {
                // Intentionally import only the non-secret IP. Any legacy
                // RemoteKey/plaintext password field is ignored by this DTO.
                RemoteIp = legacy?.RemoteIp?.Trim() ?? string.Empty
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Larkzee Chat legacy settings could not be loaded: {exception.Message}");
            return new AppSettings();
        }
    }

    private void EnsureDirectory()
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"Larkzee Chat settings directory could not be created: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Debug.WriteLine($"Larkzee Chat settings directory could not be created: {exception.Message}");
        }
    }

    private sealed class PersistedSettings
    {
        public string RemoteIp { get; set; } = string.Empty;

        public string? LocalPasswordProtected { get; set; }

        public string? RemotePasswordProtected { get; set; }

        public string? RemoteConnectionCodeProtected { get; set; }
    }

    private sealed class LegacyPersistedSettings
    {
        public string? RemoteIp { get; set; }
    }
}
