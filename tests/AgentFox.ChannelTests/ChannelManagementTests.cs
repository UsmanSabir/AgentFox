using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Modules.Web;
using AgentFox.Plugins.Channels;
using AgentFox.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ChannelManagementTests
{
    private string _directory = null!;
    private ChannelConfigStore _store = null!;
    private ChannelManager _manager = null!;
    private ChannelProviderCatalog _catalog = null!;
    private ChannelManagerHolder _holder = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Start()
    {
        _directory = Path.Combine(Path.GetTempPath(), "channel-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new(Path.Combine(_directory, "config.json"));
        File.WriteAllText(_store.ConfigFilePath, "{\"Channels\":[]}");
        _manager = new(() => (FoxAgent?)null);
        _holder = new(); _holder.Publish(_manager);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddManagementAuthentication(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Web:ManagementAuth:Enabled"] = "true",
            ["Web:ManagementAuth:ApiKeys:0:Name"] = "test",
            ["Web:ManagementAuth:ApiKeys:0:Key"] = "management-test-key",
            ["Web:ManagementAuth:ApiKeys:0:Roles:0"] = ManagementRoles.Administrator,
            ["Web:ManagementAuth:ApiKeys:1:Name"] = "viewer",
            ["Web:ManagementAuth:ApiKeys:1:Key"] = "viewer-test-key",
            ["Web:ManagementAuth:ApiKeys:1:Roles:0"] = ManagementRoles.Viewer
        }).Build());
        builder.Services.AddSingleton(_holder);
        builder.Services.AddSingleton(_store);
        _catalog = new([new DummyChannelProvider(), new FailingProvider()], NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider(), new WorkspaceManager([_directory], restrictToWorkspace: true));
        builder.Services.AddSingleton(_catalog);
        _app = builder.Build();
        _app.UseAuthentication(); _app.UseAuthorization();
        ChannelEndpoints.Map(_app.MapGroup("/api").RequireAuthorization("ManagementViewer"));
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add("X-AgentFox-Api-Key", "management-test-key");
    }

    [TestCleanup]
    public async Task Stop()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<HttpResponseMessage> Add(string name, string type = "dummy", object? config = null, string subscribe = ">") =>
        _client.PostAsJsonAsync("/api/channels", new { name, type, config = config ?? new { }, subscribe });
    private Task<HttpResponseMessage> Enable(string id, bool enabled) =>
        _client.PutAsJsonAsync($"/api/channels/{id}/enabled", new { enabled });
    private async Task<JsonElement> Snapshot() => (await _client.GetFromJsonAsync<JsonElement>("/api/channels")).GetProperty("channels");

    [TestMethod]
    public async Task Lifecycle_PreservesDisabledTopicsAndIdentityAcrossRestart_ThenDeletesPermanently()
    {
        Assert.AreEqual(HttpStatusCode.OK, (await Add("orders", subscribe: "trading.order.>")).StatusCode);
        Assert.AreEqual(1, await _manager.BroadcastAsync("first", "trading.order.accepted"));
        Assert.AreEqual(HttpStatusCode.OK, (await Enable("orders", false)).StatusCode);
        Assert.AreEqual(0, await _manager.BroadcastAsync("off", "trading.order.accepted"));
        Assert.AreEqual("disabled", (await Snapshot())[0].GetProperty("status").GetString());
        Assert.AreEqual(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/channels/orders/subscriptions", new { subscribe = "hitl.>" })).StatusCode);
        var restartedStore = new ChannelConfigStore(_store.ConfigFilePath);
        var saved = restartedStore.Read().Single();
        Assert.IsFalse(saved.Enabled);
        Assert.AreEqual("orders", saved.Name);
        Assert.AreEqual("hitl.>", saved.SubscribeSpec);
        await _manager.RemoveChannelAsync("orders"); // a fresh process has no disabled transport
        Assert.AreEqual(HttpStatusCode.OK, (await Enable("orders", true)).StatusCode);
        Assert.AreEqual(0, await _manager.BroadcastAsync("ignored", "trading.order.accepted"));
        Assert.AreEqual(1, await _manager.BroadcastAsync("question", "hitl.approval"));
        Assert.AreEqual(HttpStatusCode.OK, (await _client.DeleteAsync("/api/channels/orders")).StatusCode);
        Assert.AreEqual(0, restartedStore.Read().Count);
        Assert.AreEqual(0, _manager.Channels.Count);
    }

    [TestMethod]
    public async Task TwoSameProviderChannels_TargetOnlyTheExactId_AndRejectStaleIds()
    {
        await Add("orders"); await Add("ops");
        Assert.AreEqual(HttpStatusCode.NotFound, (await Enable("dummy", false)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/channels/missing")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _client.DeleteAsync("/api/channels/orders")).StatusCode);
        Assert.AreEqual("ops", _store.Read().Single().Name);
        Assert.IsTrue(_manager.Channels["ops"].IsConnected);
    }

    [TestMethod]
    public async Task UnnamedAndLegacyEntries_GetStableNamesBeforeArrayDeletion()
    {
        File.WriteAllText(_store.ConfigFilePath, """{"Channels":[{"Type":"dummy","Enabled":false},{"Type":"dummy","Enabled":false}]}""");
        Assert.AreEqual("channel-1", _store.Read()[1].Name);
        Assert.AreEqual(HttpStatusCode.OK, (await _client.DeleteAsync("/api/channels/channel-0")).StatusCode);
        Assert.AreEqual("channel-1", new ChannelConfigStore(_store.ConfigFilePath).Read().Single().Name);
        Assert.AreEqual(HttpStatusCode.OK, (await Enable("channel-1", true)).StatusCode);
        File.WriteAllText(_store.ConfigFilePath, """{"Channels":{"dummy_legacy":{"Enabled":false}}}""");
        Assert.AreEqual(HttpStatusCode.OK, (await Enable("dummy_legacy", true)).StatusCode);
        Assert.IsTrue(_store.Read().Single().Enabled);
    }

    [TestMethod]
    public async Task ConcurrentAdds_OneNameOneConnectionIncludingDisabledNames()
    {
        var responses = await Task.WhenAll(Add("same"), Add("SAME"));
        CollectionAssert.AreEquivalent(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).ToArray());
        Assert.AreEqual(1, _store.Read().Count);
        var id = _store.Read()[0].Name!;
        await Enable(id, false);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Add(id)).StatusCode);
    }

    [TestMethod]
    public async Task FailedConnection_IsSavedRetryable_CleansUpAndNeverLeaksProviderErrorsOrConfig()
    {
        var response = await Add("broken", "failing", new { ApiToken = "private-token" });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "Could not connect");
        Assert.IsFalse(body.Contains("private-token"));
        var channel = (FailingChannel)_manager.Channels["broken"];
        Assert.IsTrue(channel.Disconnected);
        var json = (await Snapshot()).ToString();
        Assert.IsFalse(json.Contains("private-token"));
        Assert.IsFalse(json.Contains("ApiToken"));
        Assert.IsTrue(_store.Read().Single().Enabled);
        await Enable("broken", false);
        response = await Enable("broken", true);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.IsFalse(_manager.Channels["broken"].IsEnabled);
        Assert.IsFalse(_store.Read().Single().Enabled);
    }

    [TestMethod]
    public async Task SchemaAndValidation_ExposeNoSecretsAndRejectUnapprovedFieldsOrMissingBoolean()
    {
        var schemas = await _client.GetFromJsonAsync<JsonElement>("/api/channels/providers");
        var schema = schemas.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("type").GetString() == "failing");
        Assert.IsTrue(schema.GetProperty("fields")[0].GetProperty("secret").GetBoolean());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Add("invalid", "dummy", new { BotToken = "unapproved" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Add("invalid/name")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Add("bad-topics", subscribe: "trading.>.bad")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/channels/x/enabled", new { })).StatusCode);
        Assert.AreEqual(0, _store.Read().Count);
    }

    [TestMethod]
    public async Task DisabledChannel_BlocksBroadcastAndDirectSend_EvenWhenTransportStillClaimsConnected()
    {
        var channel = new DummyChannel("live"); await channel.ConnectAsync();
        _manager.AddChannel(channel); channel.IsEnabled = false;
        Assert.AreEqual(0, await _manager.BroadcastAsync("should not send"));
        var tool = new SendToChannelTool(_manager);
        var result = await tool.ExecuteAsync(new() { ["channel_name"] = "live", ["message"] = "should not send" });
        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, channel.RecentMessages.Count);
    }

    [TestMethod]
    public async Task AgentTool_UsesTheSameLifecycleIncludingSavedDisabledChannels()
    {
        await Add("tool-channel");
        var tool = new ManageChannelTool(_manager, _catalog, _store);
        Assert.IsTrue((await tool.ExecuteAsync(new() { ["action"] = "deactivate", ["channel_name"] = "tool-channel" })).Success);
        await _manager.RemoveChannelAsync("tool-channel");
        Assert.IsTrue((await tool.ExecuteAsync(new() { ["action"] = "activate", ["channel_name"] = "tool-channel" })).Success);
        Assert.IsTrue(_manager.Channels["tool-channel"].IsConnected);
    }

    [TestMethod]
    public void SavedDeletion_DoesNotResurrectDefaults_AndCommentsAreAccepted()
    {
        var path = Path.Combine(_directory, "user.json");
        File.WriteAllText(path, "{ /* user settings */ }");
        var defaults = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Channels:0:Type"] = "dummy", ["Channels:0:Name"] = "from-defaults" }).Build();
        var store = new ChannelConfigStore(path, defaults);
        Assert.AreEqual("from-defaults", store.Read().Single().Name);
        Assert.IsNull(store.Remove("from-defaults"));
        Assert.AreEqual(0, new ChannelConfigStore(path, defaults).Read().Count);
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    [TestMethod]
    public async Task ManagementRoutes_RequireAuthentication()
    {
        _client.DefaultRequestHeaders.Remove("X-AgentFox-Api-Key");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/channels/providers")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Add("unauthorized")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Enable("x", false)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _client.DeleteAsync("/api/channels/x")).StatusCode);
    }

    [TestMethod]
    public async Task Viewer_CanReadButCannotChangeNotificationDestinations()
    {
        _client.DefaultRequestHeaders.Remove("X-AgentFox-Api-Key");
        _client.DefaultRequestHeaders.Add("X-AgentFox-Api-Key", "viewer-test-key");
        Assert.AreEqual(HttpStatusCode.OK, (await _client.GetAsync("/api/channels")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Add("denied")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Enable("x", false)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _client.DeleteAsync("/api/channels/x")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _client.PutAsJsonAsync("/api/channels/x/subscriptions", new { subscribe = ">" })).StatusCode);
    }

    [TestMethod]
    public async Task LiveOnlyChannel_ReportsSaveFailureWhileDisablingDelivery()
    {
        var channel = new DummyChannel("temporary");
        await channel.ConnectAsync(); _manager.AddChannel(channel);
        var response = await Enable("temporary", false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(result.GetProperty("persisted").GetBoolean());
        Assert.IsFalse(channel.IsEnabled);
        Assert.AreEqual("disabled", (await Snapshot())[0].GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task UnavailableProvider_RemainsVisibleAndRemovable_AndCorruptConfigIsNotAnEmptyList()
    {
        File.WriteAllText(_store.ConfigFilePath, """{"Channels":[{"Type":"uninstalled","Name":"saved","Enabled":false}]}""");
        var row = (await Snapshot())[0];
        Assert.IsFalse(row.GetProperty("providerAvailable").GetBoolean());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Enable("saved", true)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _client.DeleteAsync("/api/channels/saved")).StatusCode);
        File.WriteAllText(_store.ConfigFilePath, "null");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/api/channels")).StatusCode);
    }

    private sealed class FailingProvider : IChannelProvider
    {
        public string ChannelType => "failing";
        public string DisplayName => "Failing";
        public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() => new Dictionary<string, ChannelConfigField>
            { ["ApiToken"] = new() { Description = "Token", Required = true } };
        public (Channel?, string?) Create(Dictionary<string, string> config, ChannelCreationContext context) => (new FailingChannel(), null);
    }
    private sealed class FailingChannel : Channel
    {
        public bool Disconnected { get; private set; }
        public FailingChannel() { Type = "failing"; Name = "Failing"; }
        public override Task<bool> ConnectAsync() => throw new InvalidOperationException("https://example.invalid/private-token");
        public override Task DisconnectAsync() { Disconnected = true; IsConnected = false; return Task.CompletedTask; }
        public override Task<ChannelMessage> SendMessageAsync(string content) => throw new NotSupportedException();
        public override Task<List<ChannelMessage>> ReceiveMessagesAsync() => Task.FromResult(new List<ChannelMessage>());
    }
}
