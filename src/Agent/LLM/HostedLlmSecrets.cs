using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace AgentFox.LLM;

/// <summary>Loads the hosted account's authenticated, encrypted LLM configuration into memory.</summary>
internal static class HostedLlmSecrets
{
    public static IConfigurationBuilder AddHostedLlmSecrets(this IConfigurationBuilder builder)
    {
        var path = Environment.GetEnvironmentVariable("AGENTFOX_LLM_SECRET_FILE");
        if (string.IsNullOrEmpty(path)) return builder;

        var keyPath = Environment.GetEnvironmentVariable("AGENTFOX_LLM_SECRET_KEY_FILE")
            ?? "/run/stockji/plugin-secrets.key";
        var key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
        var blob = File.ReadAllBytes(path);
        if (key.Length != 32 || blob.Length < 12 + 16 + 2)
            throw new InvalidDataException("Invalid hosted LLM credential file.");

        var plaintext = new byte[blob.Length - 28];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plaintext.Length),
                blob.AsSpan(12 + plaintext.Length, 16), plaintext);
        try
        {
            var secrets = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(plaintext, writable: false)).Build();
            builder.AddInMemoryCollection(secrets.AsEnumerable());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
        return builder;
    }
}
