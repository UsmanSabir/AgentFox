using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins;

/// <summary>Shared constants for sensitive plugin-config values.</summary>
public static class PluginConfigSecrets
{
    /// <summary>
    /// Placeholder returned to the browser instead of a stored secret. When it round-trips back in
    /// a save request it means "leave the stored value unchanged".
    /// </summary>
    public const string Mask = "********";
}

/// <summary>Encrypts sensitive plugin-config values for at-rest storage.</summary>
public interface IPluginSecretProtector
{
    string Protect(string plaintext);
    bool TryUnprotect(string value, out string plaintext);
    bool IsProtected(string value);
}

/// <summary>
/// AES-GCM protector keyed by a random 256-bit key stored next to the plugin configs. Protects the
/// config files against casual reads and backups leaving the machine; it is NOT a defense against
/// an attacker who can already read the key file with the same user's access.
/// Value format: <c>enc:v1:base64(nonce | tag | ciphertext)</c>.
/// </summary>
public sealed class AesPluginSecretProtector : IPluginSecretProtector
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyFilePath;
    private readonly ILogger _logger;
    private readonly Lazy<byte[]> _key;

    public AesPluginSecretProtector(string keyFilePath, ILogger<AesPluginSecretProtector> logger)
    {
        _keyFilePath = keyFilePath;
        _logger = logger;
        _key = new Lazy<byte[]>(LoadOrCreateKey, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key.Value, TagSize))
            aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(payload);
    }

    public bool TryUnprotect(string value, out string plaintext)
    {
        plaintext = string.Empty;
        if (!IsProtected(value))
            return false;

        try
        {
            var payload = Convert.FromBase64String(value[Prefix.Length..]);
            if (payload.Length < NonceSize + TagSize)
                return false;

            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var cipher = payload.AsSpan(NonceSize + TagSize);
            var plainBytes = new byte[cipher.Length];

            using (var aes = new AesGcm(_key.Value, TagSize))
                aes.Decrypt(nonce, cipher, tag, plainBytes);

            plaintext = Encoding.UTF8.GetString(plainBytes);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to decrypt a stored plugin secret (key file changed or value corrupted). " +
                "The value will be treated as unset — re-enter it in the plugin settings.");
            return false;
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyFilePath))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(_keyFilePath).Trim());
            if (existing.Length == 32)
                return existing;
            _logger.LogWarning("Plugin secret key file {Path} is invalid — generating a new key.", _keyFilePath);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyFilePath)!);
        File.WriteAllText(_keyFilePath, Convert.ToBase64String(key));
        try
        {
            File.SetAttributes(_keyFilePath, File.GetAttributes(_keyFilePath) | FileAttributes.Hidden);
        }
        catch { /* best effort — hiding the key file is cosmetic */ }
        return key;
    }
}
