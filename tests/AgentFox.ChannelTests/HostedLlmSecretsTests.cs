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

            // Tampered, or encrypted under a key the account no longer has: the agent starts without the layer.
            var tampered = File.ReadAllBytes(blobPath);
            tampered[^1] ^= 1;
            File.WriteAllBytes(blobPath, tampered);
            var errors = new StringWriter();
            var fallback = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LLM:Provider"] = "Ollama" })
                .AddHostedLlmSecrets(errors).Build();
            Assert.AreEqual("Ollama", fallback["LLM:Provider"]);
            StringAssert.Contains(errors.ToString(), "AuthenticationTagMismatchException");
            Assert.IsFalse(errors.ToString().Contains("synthetic-secret"));

            File.Delete(blobPath);
            errors = new StringWriter();
            new ConfigurationBuilder().AddHostedLlmSecrets(errors);
            StringAssert.Contains(errors.ToString(), "could not be loaded");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTFOX_LLM_SECRET_FILE", previousFile);
            Environment.SetEnvironmentVariable("AGENTFOX_LLM_SECRET_KEY_FILE", previousKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void Custom_headers_are_set_on_each_request_and_reserved_names_are_refused()
    {
        var policy = new LLMFactory.CustomHeaderPolicy(new Dictionary<string, string> { ["X-Title"] = "Stockji" });
        var pipeline = System.ClientModel.Primitives.ClientPipeline.Create();
        using var message = pipeline.CreateMessage();
        policy.Apply(message);
        policy.Apply(message);   // a retry re-applies: Set replaces, never a second value
        Assert.IsTrue(message.Request.Headers.TryGetValue("X-Title", out var value));
        Assert.AreEqual("Stockji", value);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new LLMFactory.CustomHeaderPolicy(new Dictionary<string, string> { ["Authorization"] = "Bearer x" }));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new LLMFactory.CustomHeaderPolicy(new Dictionary<string, string> { ["X-Title"] = "a\r\nInjected: 1" }));

        var options = LLMFactory.OpenAiOptions("https://example.test/v1", TimeSpan.FromMinutes(10),
            new Dictionary<string, string> { ["X-Title"] = "Stockji" });
        Assert.IsNull(options.Transport);   // the SDK's own transport, so NetworkTimeout is not capped at 100 s
        Assert.AreEqual(TimeSpan.FromMinutes(10), options.NetworkTimeout);
    }
}
