using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace AgentFox.LLM;

/// <summary>Loads the hosted account's authenticated, encrypted LLM configuration into memory.</summary>
internal static class HostedLlmSecrets
{
    /// <summary>
    /// Layers the mounted credential over the user settings. A file that cannot be read or decrypted is reported on
    /// stderr and skipped rather than thrown: the AI settings are optional, and a trading agent that crash-loops at
    /// startup over them would stop managing its orders and stops. The account key can change underneath the file
    /// (a desktop import brings its own), so this is reachable without anything being corrupt.
    /// </summary>
    public static IConfigurationBuilder AddHostedLlmSecrets(this IConfigurationBuilder builder, TextWriter? errors = null)
    {
        var path = Environment.GetEnvironmentVariable("AGENTFOX_LLM_SECRET_FILE");
        if (string.IsNullOrEmpty(path)) return builder;

        var keyPath = Environment.GetEnvironmentVariable("AGENTFOX_LLM_SECRET_KEY_FILE")
            ?? "/run/stockji/plugin-secrets.key";
        try
        {
            builder.AddInMemoryCollection(Read(path, keyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                       or InvalidDataException or CryptographicException)
        {
            // The exception type only: nothing from the key or the plaintext reaches the log.
            (errors ?? Console.Error).WriteLine(
                $"[HostedLlm] The account's AI settings could not be loaded ({ex.GetType().Name}); continuing without them.");
        }
        return builder;
    }

    private static List<KeyValuePair<string, string?>> Read(string path, string keyPath)
    {
        var key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
        var plaintext = Array.Empty<byte>();
        try
        {
            var blob = File.ReadAllBytes(path);
            if (key.Length != 32 || blob.Length < 12 + 16 + 2)
                throw new InvalidDataException("Invalid hosted LLM credential file.");

            plaintext = new byte[blob.Length - 28];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plaintext.Length),
                    blob.AsSpan(12 + plaintext.Length, 16), plaintext);
            try
            {
                return new ConfigurationBuilder()
                    .AddJsonStream(new MemoryStream(plaintext, writable: false)).Build()
                    .AsEnumerable().ToList();
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                throw new InvalidDataException("Hosted LLM credential is not valid JSON.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
