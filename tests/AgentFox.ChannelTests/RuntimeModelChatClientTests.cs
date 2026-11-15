using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentFox.LLM;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class RuntimeModelChatClientTests
{
    [TestMethod]
    public async Task Switching_model_changes_the_next_request_and_attachment_capabilities()
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "OpenRouter",
            ["LLM:ApiKey"] = "synthetic-secret",
            ["LLM:Model"] = "example/text-model",
            ["LLM:BaseUrl"] = $"http://127.0.0.1:{port}/v1",
            ["LLM:User"] = "test-account",
            ["LLM:Headers:X-Title"] = "AgentFox"
        }).Build();
        using var client = new RuntimeModelChatClient(config);
        var documentReader = AttachmentDocumentReader.FromConfig(config, null, Path.GetTempPath(), client);

        async Task<(string Model, string Header, string User)> CaptureAsync()
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(context.Request.InputStream);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var response = Encoding.UTF8.GetBytes("""{"id":"test","object":"chat.completion","created":0,"model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = response.Length;
            await context.Response.OutputStream.WriteAsync(response);
            context.Response.Close();
            return (body.RootElement.GetProperty("model").GetString()!, context.Request.Headers["X-Title"]!,
                body.RootElement.GetProperty("user").GetString()!);
        }

        var first = CaptureAsync();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.AreEqual("example/text-model", (await first).Model);
        Assert.IsFalse(AttachmentSupport.Resolve(config, runtimeModel: client.Model).Images);
        Assert.IsTrue(documentReader.Handles("application/pdf"));

        Assert.IsTrue(client.TrySetModel("openai/gpt-4o", out var error), error);
        Assert.AreEqual("example/text-model", client.ConfiguredModel);
        Assert.IsTrue(AttachmentSupport.Resolve(config, runtimeModel: client.Model).Images);
        Assert.IsFalse(documentReader.Handles("application/pdf"));
        var second = CaptureAsync();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        var captured = await second;
        Assert.AreEqual("openai/gpt-4o", captured.Model);
        Assert.AreEqual("AgentFox", captured.Header);
        Assert.AreEqual("test-account", captured.User);
    }

    [TestMethod]
    public void Selector_requires_a_configured_api_key_and_rejects_invalid_ids()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "OpenRouter", ["LLM:Model"] = "initial",
            ["LLM:ApiKey"] = "synthetic-secret"
        }).Build();
        using var client = new RuntimeModelChatClient(config);
        Assert.IsFalse(client.TrySetModel("\r\nInjected", out _));
        Assert.AreEqual("initial", client.Model);
        Assert.IsFalse(client.TrySetModel(" ", out _));

        // Avoid reading a developer's real environment key in this case: Ollama is always gated.
        var localOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "Ollama", ["LLM:Model"] = "initial"
        }).Build();
        using var ollama = new RuntimeModelChatClient(localOnly);
        Assert.IsFalse(ollama.CanSwitch);
        Assert.IsFalse(ollama.TrySetModel("other", out _));
    }
}
