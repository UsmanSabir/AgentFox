using AgentFox.Modules.Loaders;
using AgentFox.Plugins.Observability;

namespace AgentFox.ChannelTests;

/// <summary>
/// Which assemblies host and plugin must SHARE, and why each one is on the list.
///
/// <para>
/// Every entry here exists because two copies of that assembly means two type identities, and in
/// each case the resulting failure is silent. This is not a style rule about duplicate DLLs — a
/// second copy of most libraries is harmless. These specific ones carry an identity across the
/// boundary, and the symptom of getting one wrong is never an exception.
/// </para>
/// </summary>
[TestClass]
public sealed class PluginTypeIdentityTests
{
    [TestMethod]
    public void TheContractAssemblyIsShared()
    {
        // Plugin module and provider types must load as the host's types or discovery drops them.
        Assert.IsTrue(PluginLoadContext.IsSharedWithHost("AgentFox.Plugins"));
    }

    [TestMethod]
    public void DiagnosticSourceIsShared_OrEveryPluginSpanIsSilentlyDropped()
    {
        // ActivitySource and Activity live here. An ActivityListener only observes spans from the
        // SAME ActivitySource type it was registered against, so a plugin holding a second copy
        // creates spans that no host listener can ever see — nothing throws, nothing logs, and the
        // configuration looks correct. Today the shared framework supplies it and this delegation
        // is a backstop; the day a plugin takes a PackageReference on OpenTelemetry.Api it stops
        // being one.
        Assert.IsTrue(PluginLoadContext.IsSharedWithHost("System.Diagnostics.DiagnosticSource"));
    }

    [TestMethod]
    public void MicrosoftExtensionsIsShared_SoHostAndPluginAgreeOnIChatClient()
    {
        Assert.IsTrue(PluginLoadContext.IsSharedWithHost("Microsoft.Extensions.AI.Abstractions"));
        Assert.IsTrue(PluginLoadContext.IsSharedWithHost("Microsoft.Extensions.Hosting.Abstractions"));
    }

    [TestMethod]
    public void UnrelatedAssembliesAreNotShared()
    {
        // The list must stay narrow. Delegating an assembly the host does not actually ship means
        // the plugin's own copy is ignored and it fails to load with a FileNotFoundException.
        Assert.IsFalse(PluginLoadContext.IsSharedWithHost("PuppeteerSharp"));
        Assert.IsFalse(PluginLoadContext.IsSharedWithHost("SomeVendor.Sdk"));
        Assert.IsFalse(PluginLoadContext.IsSharedWithHost(null));

        // Prefix rules must not match by accident.
        Assert.IsFalse(PluginLoadContext.IsSharedWithHost("System.Diagnostics.DiagnosticSource.Extra"));
        Assert.IsFalse(PluginLoadContext.IsSharedWithHost("NotMicrosoft.Extensions.AI"));
    }

    [TestMethod]
    public void TheCorrelationContextLivesInTheSharedContractAssembly()
    {
        // An AsyncLocal correlates nothing unless host and plugin read ONE static field, which
        // means one type identity. If this type ever moves into the host assembly, the trading
        // plugin gets its own copy and every correlation id it reads is null — with no error.
        var assembly = typeof(CorrelationContext).Assembly.GetName().Name;

        Assert.AreEqual("AgentFox.Plugins", assembly,
            "CorrelationContext must live in the assembly PluginLoadContext delegates to the host.");
        Assert.IsTrue(PluginLoadContext.IsSharedWithHost(assembly));
    }

    [TestMethod]
    public void TheTelemetrySourceNamesLiveInTheSharedContractAssembly()
    {
        // Same argument: the host seeds its listener list from AgentTelemetry.KnownSourceNames,
        // and a plugin emitting to a name the host does not listen to is collected by nothing.
        Assert.AreEqual("AgentFox.Plugins", typeof(AgentTelemetry).Assembly.GetName().Name);

        CollectionAssert.Contains(AgentTelemetry.KnownSourceNames.ToList(), AgentTelemetry.AgentSourceName);
        CollectionAssert.Contains(AgentTelemetry.KnownSourceNames.ToList(), AgentTelemetry.TradingSourceName);
    }
}
