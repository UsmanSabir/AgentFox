using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingAgent;

namespace AgentFox.ChannelTests;

/// <summary>
/// The community and premium trading editions are mutually exclusive deployment artifacts. Both
/// installed at once would not degrade gracefully: each entry plugin is loaded into its own
/// AssemblyLoadContext with its own copy of TradingAgent.Core, so the host would run duplicate feed,
/// watchlist-monitor and reconciliation workers, two writers against one SQLite ledger, and two
/// browser sessions against one broker account — placing duplicate orders. AddCore must refuse.
/// </summary>
[TestClass]
public class TradingEditionGuardTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [TestMethod]
    public void AddCore_ComposedTwice_Throws()
    {
        var services = new ServiceCollection();
        var config = EmptyConfig();

        TradingAgentRuntime.AddCore(services, config);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            TradingAgentRuntime.AddCore(services, config,
                TradingCompositionOptions.Community with { EditionName = "premium" }));

        // The operator has to know WHICH artifact to delete and where from; a bare "already
        // registered" would leave them guessing which of two plugin folders to remove.
        StringAssert.Contains(ex.Message, "premium");
        StringAssert.Contains(ex.Message, "mutually exclusive");
        StringAssert.Contains(ex.Message, "plugins/");
    }

    [TestMethod]
    public void AddCore_ComposedOnce_Succeeds()
    {
        var services = new ServiceCollection();

        TradingAgentRuntime.AddCore(services, EmptyConfig());

        // The engine registered, and the composition options are readable by endpoints and
        // /trading/status — the only thing that distinguishes the editions at run time, since both
        // register the module under the name "trading-agent".
        var options = services
            .Single(d => d.ServiceType == typeof(TradingCompositionOptions))
            .ImplementationInstance as TradingCompositionOptions;
        Assert.IsNotNull(options);
        Assert.AreEqual("community", options!.EditionName);
    }

    [TestMethod]
    public void MarkerTypeName_MatchesTheTypeItNames()
    {
        // The guard matches on this string because two load contexts produce two distinct Type
        // objects that never compare equal. A rename that missed the constant would silently
        // disable the guard rather than break the build, so assert they agree.
        var marker = typeof(TradingCompositionOptions).Assembly
            .GetType("TradingAgent.TradingCoreMarker");

        Assert.IsNotNull(marker, "TradingAgent.TradingCoreMarker was renamed or moved; the "
            + "duplicate-edition guard matches on that exact name and is now inert.");
    }
}
