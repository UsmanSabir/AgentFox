using AgentFox.Plugins.Channels;
using AgentFox.Plugins.Interfaces;
using TradingAgent;

namespace AgentFox.ChannelTests;

/// <summary>
/// The host's loader imposes two rules on how this plugin may be split, and breaking either fails
/// SILENTLY — the plugin loads, the dashboard works, and one capability is just quietly gone. Both
/// are asserted here rather than left to a manual check.
/// </summary>
[TestClass]
public class TradingPluginDiscoveryTests
{
    /// <summary>
    /// A plugin's channel provider is registered only when its type's assembly matches an ENABLED
    /// MODULE's assembly (Program.cs gates on `enabledModuleAssemblies.Contains(t.Assembly)`, and
    /// modules are only discovered in assemblies that ship a .deps.json). Move
    /// WhatsAppBridgeChannelProvider into TradingAgent.Core and the channel simply never appears:
    /// no error, no log, nothing to debug.
    /// </summary>
    [TestMethod]
    public void TheChannelProvider_LivesInTheSameAssemblyAsTheModule()
    {
        var moduleAssembly = typeof(TradingAgentModule).Assembly;

        var providers = moduleAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IChannelProvider).IsAssignableFrom(t))
            .ToList();

        Assert.IsTrue(providers.Count > 0,
            "The trading entry assembly exposes no IChannelProvider. If the provider moved to "
            + "TradingAgent.Core, the host will never register the whatsapp-bridge channel — and it "
            + "fails silently, with the channel just absent from the channels UI.");

        var provider = (IChannelProvider)Activator.CreateInstance(providers[0])!;
        Assert.AreEqual("whatsapp-bridge", provider.ChannelType);

        // The channel IMPLEMENTATION is shared engine code and belongs in Core, so both editions
        // reuse it; only the thin provider has to be duplicated per entry plugin.
        var channelAssembly = typeof(TradingAgent.Channel.WhatsAppBridgeChannel).Assembly;
        Assert.AreEqual("TradingAgent.Core", channelAssembly.GetName().Name);
        Assert.AreNotEqual(moduleAssembly, channelAssembly);
    }

    /// <summary>
    /// The module type must stay in the entry assembly for the loader to find it at all: discovery
    /// scans only DLLs with a sibling .deps.json, which a referenced library never gets.
    /// </summary>
    [TestMethod]
    public void TheModule_IsDiscoverableAndDelegatesToCore()
    {
        var module = new TradingAgentModule();

        Assert.IsInstanceOfType<IAppModule>(module);
        Assert.IsInstanceOfType<IAgentAwareModule>(module);
        Assert.IsInstanceOfType<IPluginUiContributor>(module);
        Assert.AreEqual("trading-agent", module.Name);

        // Both editions register under this same name so existing Modules / DisabledModules config
        // and saved plugin-config overlays keep working; EditionName is what distinguishes them.
        Assert.AreEqual("TradingAgent", typeof(TradingAgentModule).Assembly.GetName().Name);
        Assert.AreEqual("TradingAgent.Core", typeof(TradingAgentRuntime).Assembly.GetName().Name);
    }
}
