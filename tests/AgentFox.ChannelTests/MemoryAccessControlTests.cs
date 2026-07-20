using AgentFox.Agents;
using AgentFox.Memory;
using AgentFox.Plugins.Interfaces;
using AgentFox.Sessions;
using AgentFox.Tools;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class MemoryAccessControlTests
{
    private string _root = null!;
    private WorkspaceManager _workspace = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-memory-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new WorkspaceManager([_root], restrictToWorkspace: false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        FoxAgent.CurrentSessionKey.Value = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void GlobalAndSpecialistSettings_PersistAndGlobalSwitchWins()
    {
        var configuration = Configuration(new Dictionary<string, string?> { ["Memory:Enabled"] = "true" });
        var policy = new MemoryAccessPolicy(configuration, _workspace);

        policy.SetSessionOverride("web_one", false);
        Assert.IsFalse(policy.IsEnabled("web_one"));
        policy.SetSessionOverride("web_one", true);
        Assert.IsTrue(policy.IsEnabled("web_one"));

        policy.SetAgentMode("trading-agent", SpecialistMemoryMode.Isolated);
        policy.SetGlobalEnabled(false);
        Assert.IsFalse(policy.IsEnabled("web_one"), "Global off must override an enabled session.");

        var reloaded = new MemoryAccessPolicy(configuration, _workspace);
        Assert.IsFalse(reloaded.GlobalEnabled);
        Assert.AreEqual(SpecialistMemoryMode.Isolated, reloaded.GetAgentMode("trading-agent"));
    }

    [TestMethod]
    public async Task RoutedMemory_SwitchesBetweenSharedIsolatedAndDisabled()
    {
        var policy = new MemoryAccessPolicy(Configuration(), _workspace);
        policy.RegisterAgentMode("trading-agent", SpecialistMemoryMode.Shared);
        var shared = new ShortTermMemory();
        await using var routed = new RoutedMemory(
            shared,
            policy,
            "trading-agent",
            () => new HybridMemory(10, new ShortTermMemory()));
        FoxAgent.CurrentSessionKey.Value = "specialist/trading-agent/test";

        await routed.AddAsync(Entry("shared fact"));
        Assert.AreEqual(1, (await shared.GetAllAsync()).Count);

        policy.SetAgentMode("trading-agent", SpecialistMemoryMode.Isolated);
        await routed.AddAsync(Entry("isolated fact"));
        Assert.AreEqual(1, (await shared.GetAllAsync()).Count);
        CollectionAssert.Contains(
            (await routed.GetAllAsync()).Select(entry => entry.Content).ToList(),
            "isolated fact");

        policy.SetSessionOverride("specialist/trading-agent/test", false);
        Assert.IsFalse(routed.IsEnabled);
        Assert.AreEqual(0, (await routed.GetAllAsync()).Count);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => routed.AddAsync(Entry("blocked")));
    }

    [TestMethod]
    public void SessionMemoryOverride_PersistsInSessionIndexAndReloadsIntoPolicy()
    {
        var configuration = Configuration();
        var policy = new MemoryAccessPolicy(configuration, _workspace);
        var sessionConfig = new SessionConfig
        {
            SessionDirectory = "sessions",
            ArchiveDirectory = "archive/sessions",
            BackgroundCheckIntervalSeconds = 3600
        };

        using (var manager = new SessionManager(sessionConfig, _workspace, memoryPolicy: policy))
        {
            manager.GetOrCreateWebSession("main", "web_memory_test");
            Assert.IsTrue(manager.SetSessionMemoryEnabled("web_memory_test", false));
            Assert.IsFalse(policy.IsEnabled("web_memory_test"));
        }

        var reloadedPolicy = new MemoryAccessPolicy(configuration, _workspace);
        using var reloadedManager = new SessionManager(
            sessionConfig,
            _workspace,
            memoryPolicy: reloadedPolicy);
        Assert.AreEqual(false, reloadedManager.GetSession("web_memory_test")!.MemoryEnabled);
        Assert.IsFalse(reloadedPolicy.IsEnabled("web_memory_test"));
    }

    private static MemoryEntry Entry(string content) => new()
    {
        Content = content,
        Type = MemoryType.Fact,
        Importance = 0.8
    };

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
}
