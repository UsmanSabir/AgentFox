using System.Security.Cryptography;
using System.Text;
using AgentFox.LLM;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

[TestClass]
[DoNotParallelize]
public class HostedLlmSecretsTests
{
    [TestMethod]
    public void Encrypted_account_settings_override_user_settings_and_reject_tampering()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agentfox-llm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var blobPath = Path.Combine(folder, "llm.enc");
        var keyPath = Path.Combine(folder, "account.key");
        var previousFile = Environment.GetEnvironmentVariable("AGENTFOX_LLM_SECRET_FILE");
        var previousKey = Environment.GetEnvironmentVariable("AGENTFOX_LLM_SECRET_KEY_FILE");
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plain = Encoding.UTF8.GetBytes("""{"LLM":{"Provider":"OpenRouter","Model":"example/model","ApiKey":"synthetic-secret","Headers":{"X-Title":"Stockji"}}}""");
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
            File.WriteAllText(keyPath, Convert.ToBase64String(key));
            File.WriteAllBytes(blobPath, [..nonce, ..cipher, ..tag]);
            Environment.SetEnvironmentVariable("AGENTFOX_LLM_SECRET_FILE", blobPath);
            Environment.SetEnvironmentVariable("AGENTFOX_LLM_SECRET_KEY_FILE", keyPath);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LLM:Provider"] = "Ollama" })
                .AddHostedLlmSecrets().Build();
            Assert.AreEqual("OpenRouter", config["LLM:Provider"]);
            Assert.AreEqual("synthetic-secret", config["LLM:ApiKey"]);
            Assert.AreEqual("Stockji", config["LLM:Headers:X-Title"]);

            var tampered = File.ReadAllBytes(blobPath);
            tampered[^1] ^= 1;
            File.WriteAllBytes(blobPath, tampered);
            Assert.ThrowsExactly<AuthenticationTagMismatchException>(() =>
                new ConfigurationBuilder().AddHostedLlmSecrets());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTFOX_LLM_SECRET_FILE", previousFile);
            Environment.SetEnvironmentVariable("AGENTFOX_LLM_SECRET_KEY_FILE", previousKey);
            Directory.Delete(folder, recursive: true);
        }
    }
}
