using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Models;
using TradingAgent.Risk;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The trading stop's two levels (EntriesOff, FullStop), how they are read from a store more than one thing
/// writes, the BUY-only value caps, and the idempotency key that no longer depends on when a signal was built.
/// </summary>
[TestClass]
public sealed class TradingStopTests
{
    // ── Resolving the stop from the runtime store ─────────────────────────────

    private static TradingStopMode Resolve(TradingAgentOptions? opts = null, params (string Key, object? Value)[] runtime) =>
        TradingPolicyProvider.ResolveTradingStop(
            runtime.ToDictionary(r => r.Key, r => r.Value), opts ?? new TradingAgentOptions());

    [TestMethod]
    public void WithNoRuntimeKeys_TheStartupOptionsDecide()
    {
        Assert.AreEqual(TradingStopMode.Off, Resolve());
        Assert.AreEqual(TradingStopMode.EntriesOff, Resolve(new() { TradingStop = TradingStopMode.EntriesOff }));
        Assert.AreEqual(TradingStopMode.FullStop, Resolve(new() { KillSwitch = true }));
    }

    [TestMethod]
    public void TheRuntimeTradingStopKeyIsRead()
    {
        Assert.AreEqual(TradingStopMode.EntriesOff, Resolve(null, ("tradingStop", "EntriesOff")));
        Assert.AreEqual(TradingStopMode.EntriesOff, Resolve(null, ("tradingStop", "entries_off")));
        Assert.AreEqual(TradingStopMode.FullStop, Resolve(null, ("tradingStop", "FullStop")));
    }

    /// <summary>
    /// The chat command and the premium emergency button were written against the legacy key alone. A stale
    /// <c>tradingStop: Off</c> must never cancel an emergency stop pressed after it.
    /// </summary>
    [TestMethod]
    public void TheLegacyKillSwitchAlwaysWins_WhateverTheTradingStopKeySays()
    {
        Assert.AreEqual(TradingStopMode.FullStop, Resolve(null, ("tradingStop", "Off"), ("killSwitch", true)));
        Assert.AreEqual(TradingStopMode.FullStop, Resolve(null, ("tradingStop", "EntriesOff"), ("killSwitch", true)));
    }

    [TestMethod]
    public void ClearingTheLegacySwitchLeavesAnEntriesOffStopInPlace()
    {
        Assert.AreEqual(TradingStopMode.EntriesOff,
            Resolve(null, ("tradingStop", "EntriesOff"), ("killSwitch", false)));
    }

    [TestMethod]
    public void AnExplicitRuntimeFalseStillClearsAStartupKillSwitch_AsItAlwaysDid()
    {
        Assert.AreEqual(TradingStopMode.Off, Resolve(new() { KillSwitch = true }, ("killSwitch", false)));
    }

    [TestMethod]
    public void AnUnreadableTradingStopIsEntriesOff_NeverOff()
    {
        Assert.AreEqual(TradingStopMode.EntriesOff, Resolve(null, ("tradingStop", "pause everything pls")));
    }

    [TestMethod]
    public void ASnapshotCarryingTheLegacyFlagNeverReportsAWeakerStop()
    {
        var snapshot = new TradingPolicySnapshot(true, "BoundedAuto", "HIGH", false, false, false, 10,
            KillSwitch: true, "v") { TradingStop = TradingStopMode.Off };

        Assert.AreEqual(TradingStopMode.FullStop, snapshot.TradingStop);
        Assert.IsTrue(snapshot.EntriesBlocked);
    }

    [TestMethod]
    public async Task SwitchingEntriesOffChangesThePolicyVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentfox-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configs = new PluginConfigManager(root, NullLogger<PluginConfigManager>.Instance);
            var provider = new TradingPolicyProvider(Options.Create(new TradingAgentOptions()), configs);
            var before = provider.Current();

            await configs.MergeConfigAsync("trading-agent", new Dictionary<string, object?> { ["tradingStop"] = "EntriesOff" });
            var after = provider.Current();

            Assert.AreEqual(TradingStopMode.EntriesOff, after.TradingStop);
            Assert.IsFalse(after.KillSwitch, "EntriesOff is not the legacy all-orders switch");
            Assert.AreNotEqual(before.Version, after.Version,
                "an approval minted while trading was normal must not validate once entries are off");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // ── The risk engine under each stop ───────────────────────────────────────

    private static TradingRiskEngine Engine(decimal maxOrderValue = 50_000m, decimal maxBatch = 1_000_000m) =>
        new(Options.Create(new AhkConfig { MaxOrderValuePkr = maxOrderValue }),
            Options.Create(new TradingAgentOptions { AllowedSymbols = ["OGDC"], MaxBatchValuePkr = maxBatch }));

    private static TradingSignal Order(string action, int quantity = 10, decimal price = 100m) => new()
    {
        Action = action, Symbol = "OGDC", Quantity = quantity, EntryPrice = price, OrderType = "LIMIT"
    };

    [TestMethod]
    public void EntriesOff_RefusesABuy_AndLetsASellThrough()
    {
        var engine = Engine();

        var buy = engine.Validate([[Order("BUY")]], tradingStopOverride: TradingStopMode.EntriesOff);
        var sell = engine.Validate([[Order("SELL")]], tradingStopOverride: TradingStopMode.EntriesOff);

        Assert.IsFalse(buy.Allowed);
        Assert.IsTrue(buy.Violations.Any(v => v.Contains("entries are switched off")));
        Assert.IsTrue(sell.Allowed, string.Join(" ", sell.Violations));
    }

    [TestMethod]
    public void FullStop_RefusesASellToo()
    {
        Assert.IsFalse(Engine().Validate([[Order("SELL")]], tradingStopOverride: TradingStopMode.FullStop).Allowed);
    }

    [TestMethod]
    public void ATrueKillSwitchIsAFullStopEvenWhenTheModeSaysEntriesOff()
    {
        Assert.IsFalse(Engine().Validate([[Order("SELL")]], killSwitchOverride: true,
            tradingStopOverride: TradingStopMode.EntriesOff).Allowed);
    }

    [TestMethod]
    public void TheBatchCapCountsBuysOnly()
    {
        var engine = Engine(maxOrderValue: 0, maxBatch: 1_000_000m);

        // A 600,000 PKR buy alongside a 900,000 PKR sell: the batch adds no more than 600,000 of exposure.
        var mixed = engine.Validate([[Order("BUY", 6_000), Order("SELL", 9_000)]]);
        var buys = engine.Validate([[Order("BUY", 6_000), Order("BUY", 6_000)]]);

        Assert.IsTrue(mixed.Allowed, string.Join(" ", mixed.Violations));
        Assert.IsFalse(buys.Allowed);
        Assert.IsTrue(buys.Violations.Any(v => v.Contains("MaxBatchValuePkr")));
    }

    // ── The idempotency key ───────────────────────────────────────────────────

    [TestMethod]
    public void TheKeyIgnoresWhenTheSignalWasBuilt_AndNothingElse()
    {
        static string Request(DateTime built, int quantity) => System.Text.Json.JsonSerializer.Serialize(
            new[] { new[] { new TradingSignal { Action = "BUY", Symbol = "OGDC", Quantity = quantity, Timestamp = built } } },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var t = new DateTime(2026, 11, 30, 5, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual(
            TradingManager.BuildIdempotencyKey("src", Request(t, 10), "v"),
            TradingManager.BuildIdempotencyKey("src", Request(t.AddMinutes(3), 10), "v"));
        Assert.AreNotEqual(
            TradingManager.BuildIdempotencyKey("src", Request(t, 10), "v"),
            TradingManager.BuildIdempotencyKey("src", Request(t, 11), "v"));
        Assert.AreNotEqual(
            TradingManager.BuildIdempotencyKey("src", Request(t, 10), "v1"),
            TradingManager.BuildIdempotencyKey("src", Request(t, 10), "v2"));
    }

    [TestMethod]
    public void OnlyARefusalOfEveryOrderCountsAsDefinite()
    {
        static ExecutionClaim Claim(string state, params bool[] successes) => new(false, "x", state,
            System.Text.Json.JsonSerializer.Serialize(
                new TradingExecutionResult(true, false, "x", "v", "r",
                    [successes.Select(s => new OrderResult { Success = s }).ToList()]),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.IsTrue(TradingManager.IsDefiniteRefusal(Claim("failed", false)));
        Assert.IsFalse(TradingManager.IsDefiniteRefusal(Claim("failed", false, true)), "one order went out");
        Assert.IsFalse(TradingManager.IsDefiniteRefusal(Claim("accepted", true)));
        Assert.IsFalse(TradingManager.IsDefiniteRefusal(Claim("unknown", false)), "the broker may hold it");
        Assert.IsFalse(TradingManager.IsDefiniteRefusal(new ExecutionClaim(false, "x", "failed", null)));
    }

    // ── The protective stop's placement key ───────────────────────────────────

    [TestMethod]
    public void AStopsKeyNamesTheOrderItFollowsToday()
    {
        var today = new DateOnly(2026, 11, 30);
        var stop = new ProtectiveStop { StopId = "s1", Symbol = "OGDC", StopTrigger = 100m, StopLimit = 99m };

        var first = ProtectiveStopWorker.PlacementSourceKey(stop, today, 50);
        var afterOne = ProtectiveStopWorker.PlacementSourceKey(
            stop with { LastPlacedSessionDate = today, LastOrderNo = "0411XK7" }, today, 50);
        var yesterdays = ProtectiveStopWorker.PlacementSourceKey(
            stop with { LastPlacedSessionDate = today.AddDays(-1), LastOrderNo = "0411XK3" }, today, 50);

        Assert.AreNotEqual(first, afterOne, "a re-place after today's order left the book is a new instruction");
        Assert.AreEqual(first, yesterdays, "yesterday's order is gone with yesterday; today starts first again");
        StringAssert.EndsWith(afterOne, ":after:0411XK7");
    }
}
