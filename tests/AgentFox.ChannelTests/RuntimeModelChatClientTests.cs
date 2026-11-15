using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentFox.LLM;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Research;

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
        Assert.AreEqual("openai/gpt-4o",
            (client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId);
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
    public async Task Trading_assessment_cache_is_separate_for_each_runtime_model()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "OpenRouter", ["LLM:ApiKey"] = "synthetic-secret",
            ["LLM:Model"] = "example/first", ["LLM:BaseUrl"] = $"http://127.0.0.1:{port}/v1"
        }).Build();
        using var client = new RuntimeModelChatClient(config);
        var assessments = new StockAssessmentService(client, NullLogger<StockAssessmentService>.Instance);
        var request = new StockAssessmentRequest
        {
            Symbol = "OGDC", Evidence = new { symbol = "OGDC" },
            CacheKey = StockAssessmentService.CacheKeyFor("OGDC", 309m, "1D")
        };

        async Task<string> AnswerAsync()
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(context.Request.InputStream);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var model = body.RootElement.GetProperty("model").GetString()!;
            var content = """{"confidence":"HIGH","confidence_score":80,"recommendation":"PROCEED","rationale":"ok","supporting_factors":[],"risk_factors":[],"invalidation_level":null}""";
            var response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                id = "test", @object = "chat.completion", created = 0, model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } }
            }));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = response.Length;
            await context.Response.OutputStream.WriteAsync(response);
            context.Response.Close();
            return model;
        }

        var firstCall = AnswerAsync();
        Assert.IsFalse((await assessments.AssessAsync(request)).FromCache);
        Assert.AreEqual("example/first", await firstCall);
        Assert.IsTrue((await assessments.AssessAsync(request)).FromCache);

        Assert.IsTrue(client.TrySetModel("example/second", out var error), error);
        var secondCall = AnswerAsync();
        Assert.IsFalse((await assessments.AssessAsync(request)).FromCache);
        Assert.AreEqual("example/second", await secondCall);
        Assert.IsTrue((await assessments.AssessAsync(request)).FromCache);
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

    [TestMethod]
    public void Anthropic_agent_without_a_base_url_keeps_its_own_endpoint_after_a_switch()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "Anthropic", ["LLM:ApiKey"] = "synthetic-secret", ["LLM:Model"] = "claude-first"
        }).Build();
        using var client = new RuntimeModelChatClient(config);
        var startup = (ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!;
        Assert.IsNotNull(startup.ProviderUri);
        Assert.AreEqual("api.anthropic.com", startup.ProviderUri!.Host);

        Assert.IsTrue(client.TrySetModel("claude-second", out var error), error);
        var switched = (ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!;
        Assert.AreEqual("claude-second", switched.DefaultModelId);
        Assert.AreEqual(startup.ProviderUri, switched.ProviderUri);
    }

    [TestMethod]
    public void Azure_switch_is_built_like_the_startup_client()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "AzureOpenAI", ["LLM:ApiKey"] = "synthetic-secret", ["LLM:Model"] = "deployment-one",
            ["LLM:BaseUrl"] = "https://example.openai.azure.com/"
        }).Build();
        using var client = new RuntimeModelChatClient(config);
        var startupType = client.GetService(typeof(OpenAI.Chat.ChatClient))!.GetType();
        Assert.IsTrue(client.TrySetModel("deployment-two", out var error), error);
        var switched = client.GetService(typeof(OpenAI.Chat.ChatClient))!;
        Assert.AreEqual(startupType, switched.GetType());
        Assert.AreEqual(typeof(Azure.AI.OpenAI.AzureOpenAIClient).Assembly, switched.GetType().Assembly);
        Assert.AreEqual("deployment-two",
            ((ChatClientMetadata)client.GetService(typeof(ChatClientMetadata))!).DefaultModelId);
    }

    [TestMethod]
    public void GetService_for_the_chat_client_returns_the_wrapper_not_an_inner_client()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "OpenRouter", ["LLM:ApiKey"] = "synthetic-secret", ["LLM:Model"] = "initial"
        }).Build();
        using var client = new RuntimeModelChatClient(config);
        Assert.AreSame(client, client.GetService(typeof(IChatClient)));
        Assert.AreSame(client, client.GetService(typeof(RuntimeModelChatClient)));
        Assert.IsTrue(client.TrySetModel("other", out var error), error);
        Assert.AreSame(client, client.GetService(typeof(IChatClient)));
        Assert.IsNotNull(client.GetService(typeof(ChatClientMetadata)));
    }
}
