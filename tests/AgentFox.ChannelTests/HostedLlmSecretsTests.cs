using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentFox.LLM;
using Microsoft.Extensions.AI;
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

    [TestMethod]
    public async Task OpenRouter_requests_include_the_hosted_account_slug_in_the_body()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "OpenRouter",
            ["LLM:Model"] = "example/model",
            ["LLM:ApiKey"] = "synthetic-secret",
            ["LLM:BaseUrl"] = $"http://127.0.0.1:{port}/v1",
            ["LLM:User"] = "magma",
            ["Models:LM:Provider"] = "OpenRouter",
            ["Models:LM:Model"] = "example/other-model",
            ["Models:LM:ApiKey"] = "synthetic-secret",
            ["Models:LM:BaseUrl"] = $"http://127.0.0.1:{port}/v1"
        }).Build();

        async Task<string> CaptureRequestAsync(bool streaming)
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var response = Encoding.UTF8.GetBytes(streaming
                ? """data: {"id":"test","object":"chat.completion.chunk","created":0,"model":"example/model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""" + "\n\ndata: [DONE]\n\n"
                : """{"id":"test","object":"chat.completion","created":0,"model":"example/model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
            context.Response.ContentType = streaming ? "text/event-stream" : "application/json";
            context.Response.ContentLength64 = response.Length;
            await context.Response.OutputStream.WriteAsync(response);
            context.Response.Close();
            return body;
        }

        var previousUser = Environment.GetEnvironmentVariable("LLM__User");
        try
        {
            Environment.SetEnvironmentVariable("LLM__User", "magma");
            foreach (var client in new[] {
                LLMFactory.CreateFromConfiguration(configuration),
                LLMFactory.CreateWithModelOverride(configuration, "LM")!,
                LLMFactory.CreateChatClient(new AgentFox.Agents.ModelConfig {
                    Provider = "OpenRouter", Model = "example/model", ApiKey = "synthetic-secret",
                    BaseUrl = $"http://127.0.0.1:{port}/v1" })! })
            {
                using (client)
                {
                    var capture = CaptureRequestAsync(false);
                    await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
                    using var body = JsonDocument.Parse(await capture);
                    Assert.AreEqual("magma", body.RootElement.GetProperty("user").GetString());

                    capture = CaptureRequestAsync(true);
                    await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")])) { }
                    using var streamedBody = JsonDocument.Parse(await capture);
                    Assert.AreEqual("magma", streamedBody.RootElement.GetProperty("user").GetString());
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("LLM__User", previousUser); }
    }
}
