using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Risk;

namespace AgentFox.ChannelTests;

/// <summary>
/// Risk validation for native stop-loss orders.
///
/// <para>
/// The load-bearing rule is the limit's direction. A stop is a trigger plus a limit, and the limit
/// has to sit on the side the market will be moving when the trigger fires: below it for a
/// protective sell, above it for a buy stop. Getting that backwards produces an order that can never
/// fill — which is materially worse than having no stop at all, because it looks like protection
/// right up until the moment it is needed.
/// </para>
/// </summary>
[TestClass]
public sealed class StopLossOrderTests
{
    [TestMethod]
    public void StopLossIsAnAcceptedOrderType()
    {
        var result = Validate(Stop("SELL", trigger: 300m, limit: 297m));
        Assert.IsTrue(result.Allowed, string.Join(" | ", result.Violations));
    }

    [TestMethod]
    public void SellStop_LimitAboveTrigger_IsRejected()
    {
        var result = Validate(Stop("SELL", trigger: 300m, limit: 305m));

        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(
            result.Violations.Any(v => v.Contains("at or below", StringComparison.OrdinalIgnoreCase)),
            "A sell stop whose limit sits above the trigger cannot fill in the fall that triggered it.");
    }

    [TestMethod]
    public void BuyStop_LimitBelowTrigger_IsRejected()
    {
        var result = Validate(Stop("BUY", trigger: 300m, limit: 295m));

        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(
            result.Violations.Any(v => v.Contains("at or above", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SellStop_LimitEqualToTrigger_IsAllowed()
    {
        // Legal, though it risks not filling — that trade-off is the operator's to make via
        // Ahk.StopLimitSlippagePercent, not something validation should forbid.
        Assert.IsTrue(Validate(Stop("SELL", trigger: 300m, limit: 300m)).Allowed);
    }

    [TestMethod]
    public void StopLoss_WithoutATrigger_IsRejected()
    {
        var order = Stop("SELL", trigger: null, limit: 290m);
        var result = Validate(order);

        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("limit price is required", StringComparison.OrdinalIgnoreCase)),
            "A stop with no trigger price has nothing to fire on.");
    }

    [TestMethod]
    public void StopLoss_StillObeysTheSymbolAllowList()
    {
        var order = Stop("SELL", trigger: 300m, limit: 297m);
        order.Symbol = "NOTALLOWED";

        var result = Validate(order);

        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("selected execution universe", StringComparison.Ordinal)),
            "A stop order is still an order: it cannot bypass the tradable universe.");
    }

    [TestMethod]
    public void StopLoss_StillObeysTheOrderValueCap()
    {
        var order = Stop("SELL", trigger: 300m, limit: 297m);
        order.Quantity = 100_000;

        var result = Validate(order);

        Assert.IsFalse(result.Allowed);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("MaxOrderValuePkr", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void StopLoss_WithoutAnExplicitLimit_IsAllowed()
    {
        // The broker derives the limit from the trigger and the configured slippage allowance, so an
        // absent limit is a normal request rather than an incomplete one.
        var order = Stop("SELL", trigger: 300m, limit: null);
        Assert.IsTrue(Validate(order).Allowed, string.Join(" | ", Validate(order).Violations));
    }

    [TestMethod]
    public void WatchlistSource_UsesResolvedSymbolsInsteadOfAllowedSymbols()
    {
        var engine = CreateEngine(new TradingAgentOptions
        {
            AllowedSymbols = ["OGDC"],
            ExecutionUniverseSource = TradingExecutionUniverseSource.Watchlist
        });
        var hbl = Stop("SELL", trigger: 300m, limit: 297m);
        hbl.Symbol = "HBL";

        Assert.IsTrue(engine.Validate([[hbl]], executionUniverseOverride: ["HBL"]).Allowed);
        Assert.IsFalse(engine.Validate([[Stop("SELL", 300m, 297m)]],
            executionUniverseOverride: ["HBL"]).Allowed,
            "AllowedSymbols must not leak into Watchlist mode.");
    }

    [TestMethod]
    public void WatchlistSource_EmptyOrUnresolvedNeverBecomesUnrestricted()
    {
        var engine = CreateEngine(new TradingAgentOptions
        {
            AllowedSymbols = ["OGDC"],
            ExecutionUniverseSource = TradingExecutionUniverseSource.Watchlist,
            RequireConfiguredSymbols = false
        });

        Assert.IsFalse(engine.Validate([[Stop("SELL", 300m, 297m)]]).Allowed,
            "A missing authoritative watchlist must fail closed.");
        Assert.IsFalse(engine.Validate([[Stop("SELL", 300m, 297m)]],
            executionUniverseOverride: []).Allowed,
            "An explicitly empty watchlist is an empty set, not a wildcard.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TradingSignal Stop(string action, decimal? trigger, decimal? limit) => new()
    {
        Action = action,
        Symbol = "OGDC",
        Quantity = 10,
        OrderType = "STOPLOSS",
        EntryPrice = trigger,
        LimitPrice = limit
    };

    private static RiskValidationResult Validate(TradingSignal order)
    {
        var engine = CreateEngine(new TradingAgentOptions
        {
            AllowedSymbols = ["OGDC"],
            MaxBatchValuePkr = 250_000m
        });

        return engine.Validate([[order]]);
    }

    private static TradingRiskEngine CreateEngine(TradingAgentOptions options) => new(
        Options.Create(new AhkConfig { MaxOrderValuePkr = 50_000m }),
        Options.Create(options));
}
