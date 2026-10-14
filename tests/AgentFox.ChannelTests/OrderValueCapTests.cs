using AgentFox.Plugins;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Risk;

namespace AgentFox.ChannelTests;

/// <summary>
/// The order and batch value caps, as a hosted user meets them: set in Settings, applied to the next order, and 0
/// meaning no cap. Until 2026-10-14 the order cap was read from the startup file alone, so a user who cannot edit
/// that file could never place an order above PKR 50,000, and typing 0 would have refused every order.
/// </summary>
[TestClass]
public sealed class OrderValueCapTests
{
    private static IReadOnlyList<IReadOnlyList<TradingSignal>> Order(decimal value) =>
        [new List<TradingSignal> { new() { Action = "BUY", Symbol = "OGDC", Quantity = 1000, EntryPrice = value / 1000, OrderType = "LIMIT" } }];

    private static readonly IOptions<TradingAgentOptions> Agent =
        Options.Create(new TradingAgentOptions { AllowedSymbols = ["OGDC"] });

    private sealed class Live(AhkConfig current) : IRuntimePluginOptions<AhkConfig> { public AhkConfig Current => current; }

    [TestMethod]
    public void The_order_cap_still_refuses_above_it()
    {
        var engine = new TradingRiskEngine(Options.Create(new AhkConfig()), Agent);
        var result = engine.Validate(Order(60_000));
        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("MaxOrderValuePkr")));
    }

    [TestMethod]
    public void A_cap_set_in_Settings_applies_over_the_startup_file()
    {
        var engine = new TradingRiskEngine(Options.Create(new AhkConfig()), Agent,
            new Live(new AhkConfig { MaxOrderValuePkr = 200_000 }));
        Assert.IsTrue(engine.Validate(Order(150_000)).Allowed);
        Assert.IsFalse(engine.Validate(Order(210_000)).Allowed);
    }

    [TestMethod]
    public void Zero_means_no_cap_for_the_order_and_the_batch()
    {
        var engine = new TradingRiskEngine(Options.Create(new AhkConfig { MaxOrderValuePkr = 0 }),
            Options.Create(new TradingAgentOptions { AllowedSymbols = ["OGDC"], MaxBatchValuePkr = 0 }));
        Assert.IsTrue(engine.Validate(Order(5_000_000)).Allowed);
    }

    [TestMethod]
    public void The_batch_cap_still_refuses_a_single_order_above_it()
    {
        var engine = new TradingRiskEngine(Options.Create(new AhkConfig { MaxOrderValuePkr = 0 }), Agent);
        var result = engine.Validate(Order(300_000));
        Assert.IsFalse(result.Allowed, "the default batch cap is PKR 250,000, and one order is a batch of one");
        Assert.IsTrue(result.Violations.Any(v => v.Contains("MaxBatchValuePkr")));
    }
}
