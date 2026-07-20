using System.Text.Json;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFox.ChannelTests;

/// <summary>
/// Verifies PluginConfigManager normalizes JsonElement values to CLR primitives.
/// Regression guard: TradingAgent reads config with `is bool`/`is string` pattern matches,
/// which silently fail against the JsonElement values that System.Text.Json / ASP.NET binding
/// produce — making the "configurable from web UI" feature a no-op without normalization.
/// </summary>
[TestClass]
public class PluginConfigManagerTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The exact shape ASP.NET request binding produces: object values are JsonElement.</summary>
    private static Dictionary<string, object?> AsJsonBoundConfig(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    [TestMethod]
    public async Task SavePath_NormalizesJsonElement_ToClrPrimitives()
    {
        var mgr = new PluginConfigManager(_dir, NullLogger<PluginConfigManager>.Instance);

        var incoming = AsJsonBoundConfig("""{ "autoExecute": true, "minConfidence": "HIGH" }""");
        // Pre-condition: raw bound values are JsonElement, NOT bool/string.
        Assert.IsInstanceOfType(incoming["autoExecute"], typeof(JsonElement));

        await mgr.SaveConfigAsync("trading-agent", incoming);

        var cfg = mgr.GetConfig("trading-agent");
        Assert.IsTrue(cfg["autoExecute"] is bool, "autoExecute should be a CLR bool after save");
        Assert.IsTrue((bool)cfg["autoExecute"]!);
        Assert.IsTrue(cfg["minConfidence"] is string, "minConfidence should be a CLR string after save");
        Assert.AreEqual("HIGH", (string)cfg["minConfidence"]!);
    }

    [TestMethod]
    public async Task LoadPath_NormalizesJsonElement_AfterDiskReload()
    {
        // Persist with one instance...
        var writer = new PluginConfigManager(_dir, NullLogger<PluginConfigManager>.Instance);
        await writer.SaveConfigAsync("trading-agent",
            AsJsonBoundConfig("""{ "autoExecute": false, "minConfidence": "MEDIUM" }"""));

        // ...then reload from disk with a fresh instance (exercises the deserialize path).
        var reader = new PluginConfigManager(_dir, NullLogger<PluginConfigManager>.Instance);
        var cfg = reader.GetConfig("trading-agent");

        Assert.IsTrue(cfg["autoExecute"] is bool, "autoExecute should be a CLR bool after disk reload");
        Assert.IsFalse((bool)cfg["autoExecute"]!);
        Assert.IsTrue(cfg["minConfidence"] is string, "minConfidence should be a CLR string after disk reload");
        Assert.AreEqual("MEDIUM", (string)cfg["minConfidence"]!);
    }

    [TestMethod]
    public async Task MergePath_PreservesExistingKeys_AndNormalizes()
    {
        var mgr = new PluginConfigManager(_dir, NullLogger<PluginConfigManager>.Instance);
        await mgr.SaveConfigAsync("trading-agent",
            AsJsonBoundConfig("""{ "autoExecute": true, "minConfidence": "HIGH" }"""));

        // Merge only flips one key; the other must survive.
        await mgr.MergeConfigAsync("trading-agent",
            AsJsonBoundConfig("""{ "autoExecute": false }"""));

        var cfg = mgr.GetConfig("trading-agent");
        Assert.IsTrue(cfg["autoExecute"] is bool b && b == false);
        Assert.IsTrue(cfg["minConfidence"] is string s && s == "HIGH", "merge must preserve untouched keys");
    }
}
