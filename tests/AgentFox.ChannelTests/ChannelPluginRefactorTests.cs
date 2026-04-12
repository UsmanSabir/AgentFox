using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using AgentFox.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Json;

namespace AgentFox.ChannelTests;

[TestClass]
public class ChannelPluginRefactorTests
{
    [TestMethod]
    public void ChannelConfiguration_ReadsCanonicalArray_AndPrefersItOverLegacyKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:0:Type"] = "Telegram",
                ["Channels:0:BotToken"] = "canonical-token",
                ["Channels:Telegram1:BotToken"] = "legacy-token"
            })
            .Build();

        var entries = ChannelConfiguration.ReadEntries(config);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("telegram", entries[0].Type);
        Assert.AreEqual("canonical-token", entries[0].Config["BotToken"]);
    }

    [TestMethod]
    public void ChannelConfiguration_ReadsLegacyObject_AndInfersTypeFromKey()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:Telegram1:BotToken"] = "legacy-token",
                ["Channels:Telegram1:PollingTimeoutSeconds"] = "45"
            })
            .Build();

        var entries = ChannelConfiguration.ReadEntries(config);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("telegram", entries[0].Type);
        Assert.AreEqual("Telegram1", entries[0].Key);
        Assert.AreEqual("legacy-token", entries[0].Config["BotToken"]);
    }

    [TestMethod]
    public void ChannelProviderCatalog_ExposesProviderSchemas_AndReturnsUnknownTypeError()
    {
        var catalog = CreateCatalog(new IChannelProvider[]
        {
            new TelegramChannelProvider(),
            new SlackChannelProvider()
        });

        CollectionAssert.AreEquivalent(
            new[] { "slack", "telegram" },
            catalog.SupportedTypes.ToArray());

        var telegramSchema = catalog.GetConfigSchema("telegram");
        Assert.IsNotNull(telegramSchema);
        Assert.IsTrue(telegramSchema.ContainsKey("BotToken"));
        Assert.IsTrue(telegramSchema["BotToken"].Required);

        var (channel, error) = catalog.Create("unknown", new Dictionary<string, string>());
        Assert.IsNull(channel);
        StringAssert.Contains(error!, "Unknown channel type");
    }

    [TestMethod]
    public void ManageChannelTool_UsesProviderBackedTypesInParameters()
    {
        var tempConfig = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempConfig, "{ }");
            var catalog = CreateCatalog(new IChannelProvider[]
            {
                new TelegramChannelProvider(),
                new DiscordChannelProvider()
            });

            var tool = new ManageChannelTool(
                new ChannelManager(() => (FoxAgent?)null),
                catalog,
                tempConfig,
                NullLogger<ManageChannelTool>.Instance);

            var channelTypeParam = tool.Parameters["channel_type"];
            CollectionAssert.AreEquivalent(
                new[] { "discord", "telegram" },
                channelTypeParam.EnumValues!.ToArray());
            StringAssert.Contains(tool.Description, "telegram");
            StringAssert.Contains(tool.Description, "discord");
        }
        finally
        {
            File.Delete(tempConfig);
        }
    }

    [TestMethod]
    public async Task WebChannelsEndpoint_ReturnsStableChannelType()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ChannelManagerHolder>();

        var app = builder.Build();
        app.MapGet("/api/channels", (ChannelManagerHolder channelHolder) =>
        {
            var manager = channelHolder.Manager;
            if (manager == null)
                return Results.Ok(new { ready = false, channels = Array.Empty<object>() });

            var channels = manager.Channels.Values.Select(ch => new
            {
                id = ch.ChannelId,
                name = ch.Name,
                type = ch.Type,
                isConnected = ch.IsConnected,
                status = ch.IsConnected ? "connected" : "disconnected"
            });

            return Results.Ok(new
            {
                ready = true,
                channels,
                total = manager.Channels.Count,
                connected = manager.Channels.Values.Count(c => c.IsConnected)
            });
        });

        var holder = app.Services.GetRequiredService<ChannelManagerHolder>();
        var manager = new ChannelManager(() => (FoxAgent?)null);
        manager.AddChannel(new FakeChannel("telegram", "Telegram", "telegram_main", connected: true));
        holder.Publish(manager);

        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            var response = await client.GetFromJsonAsync<ChannelsResponse>("/api/channels");

            Assert.IsNotNull(response);
            Assert.IsTrue(response.ready);
            Assert.AreEqual("telegram", response.channels.Single().type);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static ChannelProviderCatalog CreateCatalog(IEnumerable<IChannelProvider> providers)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var workspaceManager = new WorkspaceManager(new[] { Path.GetTempPath() }, restrictToWorkspace: false);
        return new ChannelProviderCatalog(
            providers,
            NullLoggerFactory.Instance,
            services,
            workspaceManager);
    }

    private sealed class FakeChannel : Channel
    {
        public FakeChannel(string type, string name, string channelId, bool connected)
        {
            Type = type;
            Name = name;
            ChannelId = channelId;
            IsConnected = connected;
        }

        public override Task<bool> ConnectAsync() => Task.FromResult(true);

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task<ChannelMessage> SendMessageAsync(string content) =>
            Task.FromResult(new ChannelMessage { ChannelId = ChannelId, Content = content });

        public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
            Task.FromResult(new List<ChannelMessage>());
    }

    private sealed class ChannelsResponse
    {
        public bool ready { get; set; }
        public List<ChannelInfoResponse> channels { get; set; } = new();
    }

    private sealed class ChannelInfoResponse
    {
        public string type { get; set; } = string.Empty;
    }
}
