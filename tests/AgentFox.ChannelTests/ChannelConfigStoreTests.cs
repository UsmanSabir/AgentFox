using System.Text.Json.Nodes;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

/// <summary>
/// Persistence of channel subscriptions. The risk here is not the matcher — it is rewriting the
/// right entry in a file that may contain several channels of the same type, and producing config
/// that reads back as what was saved.
/// </summary>
[TestClass]
public class ChannelConfigStoreTests
{
    private string _path = string.Empty;

    [TestInitialize]
    public void CreateTempConfig() =>
        _path = Path.Combine(Path.GetTempPath(), $"agentfox-channels-{Guid.NewGuid():N}.json");

    [TestCleanup]
    public void DeleteTempConfig()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [TestMethod]
    public void SetSubscription_WritesFiltersThatReadBackIdentically()
    {
        WriteConfig("""
            { "Channels": [ { "Type": "telegram", "Name": "tg-orders", "BotToken": "t" } ] }
            """);

        var store = new ChannelConfigStore(_path);
        var channel = new StubChannel("telegram", "tg-orders");
        var subscription = TopicSubscription.Parse("trading.order.>, hitl.>");

        Assert.IsNull(store.SetSubscription(channel, subscription));

        var entry = ReadEntries().Single();
        Assert.AreEqual("trading.order.>, hitl.>", entry.SubscribeSpec);

        var roundTripped = TopicSubscription.Parse(entry.SubscribeSpec);
        CollectionAssert.AreEqual(subscription.Filters.ToArray(), roundTripped.Filters.ToArray());
        Assert.IsTrue(roundTripped.Matches("trading.order.accepted"));
        Assert.IsFalse(roundTripped.Matches("agent.notify"));
    }

    [TestMethod]
    public void SetSubscription_TargetsTheNamedEntry_NotTheFirstOfThatType()
    {
        // Two Telegram bots: matching on type alone would rewrite whichever came first, which is
        // the same identity bug that made subscriptions unstorable to begin with.
        WriteConfig("""
            {
              "Channels": [
                { "Type": "telegram", "Name": "tg-ops",    "BotToken": "a" },
                { "Type": "telegram", "Name": "tg-orders", "BotToken": "b" }
              ]
            }
            """);

        var store = new ChannelConfigStore(_path);
        Assert.IsNull(store.SetSubscription(
            new StubChannel("telegram", "tg-orders"), TopicSubscription.Parse("trading.order.>")));

        var entries = ReadEntries();
        Assert.IsNull(entries.Single(e => e.Name == "tg-ops").SubscribeSpec);
        Assert.AreEqual("trading.order.>", entries.Single(e => e.Name == "tg-orders").SubscribeSpec);
    }

    [TestMethod]
    public void SetSubscription_FallsBackToTypeWhenNoEntryIsNamed()
    {
        WriteConfig("""
            { "Channels": [ { "Type": "telegram", "BotToken": "t" } ] }
            """);

        var store = new ChannelConfigStore(_path);

        Assert.IsNull(store.SetSubscription(
            new StubChannel("telegram", "telegram"), TopicSubscription.Parse("hitl.>")));
        Assert.AreEqual("hitl.>", ReadEntries().Single().SubscribeSpec);
    }

    [TestMethod]
    public void SetSubscription_ReportsWhenTheChannelHasNoEntryToUpdate()
    {
        // A channel added at runtime and never saved: the caller has to be able to say the change
        // is live but will not survive a restart, so this must not report success.
        WriteConfig("""{ "Channels": [] }""");

        var error = new ChannelConfigStore(_path)
            .SetSubscription(new StubChannel("discord", "dc"), TopicSubscription.All);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "No matching entry");
    }

    [TestMethod]
    public void SetSubscription_ReportsUnreadableConfigRatherThanThrowing()
    {
        File.WriteAllText(_path, "not json at all");

        var error = new ChannelConfigStore(_path)
            .SetSubscription(new StubChannel("telegram", "telegram"), TopicSubscription.All);

        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void SetSubscription_LeavesOtherKeysOnTheEntryUntouched()
    {
        WriteConfig("""
            {
              "Channels": [
                { "Type": "telegram", "Name": "tg", "BotToken": "secret", "Enabled": true }
              ],
              "Tools": { "Channels": true }
            }
            """);

        var store = new ChannelConfigStore(_path);
        Assert.IsNull(store.SetSubscription(
            new StubChannel("telegram", "tg"), TopicSubscription.Parse("agent.>")));

        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        var entry = root["Channels"]!.AsArray()[0]!.AsObject();

        Assert.AreEqual("secret", entry["BotToken"]!.GetValue<string>());
        Assert.IsTrue(entry["Enabled"]!.GetValue<bool>());
        Assert.IsTrue(root["Tools"]!["Channels"]!.GetValue<bool>());
    }

    [TestMethod]
    public void SetSubscription_NormalizesTheLegacyObjectShapeIntoTheArrayForm()
    {
        WriteConfig("""
            { "Channels": { "telegram_main": { "BotToken": "t" } } }
            """);

        var store = new ChannelConfigStore(_path);
        Assert.IsNull(store.SetSubscription(
            new StubChannel("telegram", "telegram_main"), TopicSubscription.Parse("trading.>")));

        var root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.IsInstanceOfType<JsonArray>(root["Channels"]);

        var entry = ReadEntries().Single();
        Assert.AreEqual("telegram_main", entry.Name);
        Assert.AreEqual("trading.>", entry.SubscribeSpec);
    }

    [TestMethod]
    public void Remove_DropsTheNamedEntryOnly()
    {
        WriteConfig("""
            {
              "Channels": [
                { "Type": "telegram", "Name": "tg-ops",    "BotToken": "a" },
                { "Type": "telegram", "Name": "tg-orders", "BotToken": "b" }
              ]
            }
            """);

        Assert.IsNull(new ChannelConfigStore(_path).Remove(new StubChannel("telegram", "tg-orders")));

        var entries = ReadEntries();
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("tg-ops", entries[0].Name);
    }

    [TestMethod]
    public void Add_ThenSetSubscription_RoundTripsThroughTheConfigReader()
    {
        WriteConfig("""{ "Channels": [] }""");

        var store = new ChannelConfigStore(_path);
        Assert.IsNull(store.Add("telegram", new Dictionary<string, string>
        {
            ["Name"] = "tg-orders",
            ["BotToken"] = "t"
        }));

        Assert.IsNull(store.SetSubscription(
            new StubChannel("telegram", "tg-orders"), TopicSubscription.Parse("trading.order.>")));

        var entry = ReadEntries().Single();
        Assert.AreEqual("telegram", entry.Type);
        Assert.AreEqual("tg-orders", entry.Name);
        Assert.AreEqual("trading.order.>", entry.SubscribeSpec);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WriteConfig(string json) => File.WriteAllText(_path, json);

    private List<ChannelConfigurationEntry> ReadEntries() =>
        ChannelConfiguration.ReadEntries(
            new ConfigurationBuilder().AddJsonFile(_path, optional: false).Build());

    private sealed class StubChannel : Channel
    {
        public StubChannel(string type, string channelId)
        {
            Type = type;
            Name = type;
            ChannelId = channelId;
        }

        public override Task<bool> ConnectAsync() => Task.FromResult(true);

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task<ChannelMessage> SendMessageAsync(string content) =>
            Task.FromResult(new ChannelMessage { ChannelId = ChannelId, Content = content });

        public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
            Task.FromResult(new List<ChannelMessage>());
    }
}
