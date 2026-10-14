using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;
using AgentFox.Plugins;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;

namespace TradingAgent.Risk;

/// <summary>Deterministic pre-trade validation that cannot be bypassed by agent prompting.</summary>
public sealed partial class TradingRiskEngine : ITradingRiskEngine
{
    private readonly IOptions<AhkConfig> _options;
    private readonly IOptions<TradingAgentOptions> _agentOptions;
    private readonly IRuntimePluginOptions<AhkConfig>? _liveBroker;
    private readonly PluginConfigManager? _configs;

    /// <param name="liveBroker">The broker settings as edited in the browser. The order-value cap is read from here
    /// so a change in Settings applies to the next order; without it only the startup file counted, and a hosted
    /// user, who cannot edit that file, could never raise the cap (2026-10-14).</param>
    /// <param name="configs">For the batch cap's browser override, the "trading-agent" settings.</param>
    public TradingRiskEngine(
        IOptions<AhkConfig> options,
        IOptions<TradingAgentOptions> agentOptions,
        IRuntimePluginOptions<AhkConfig>? liveBroker = null,
        PluginConfigManager? configs = null)
    {
        _options = options;
        _agentOptions = agentOptions;
        _liveBroker = liveBroker;
        _configs = configs;
    }

    /// <summary>The largest batch value allowed, browser setting first; 0 or less means no cap.</summary>
    private decimal MaxBatchValue(TradingAgentOptions agent)
    {
        if (_configs?.GetConfig("trading-agent") is { } runtime
            && runtime.TryGetValue("maxBatchValuePkr", out var raw) && ToDecimal(raw) is { } overridden)
            return overridden;
        return agent.MaxBatchValuePkr;
    }

    private static decimal? ToDecimal(object? value) => value switch
    {
        null => null,
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        long l => l,
        int i => i,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDecimal(out var d) => d,
        JsonElement { ValueKind: JsonValueKind.String } e
            when decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public RiskValidationResult Validate(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        bool? killSwitchOverride = null,
        IReadOnlyCollection<string>? executionUniverseOverride = null,
        IReadOnlyCollection<string>? liquidationUniverseOverride = null)
    {
        var cfg = _liveBroker?.Current ?? _options.Value;
        var agent = _agentOptions.Value;
        var maxBatch = MaxBatchValue(agent);
        var violations = new List<string>();
        var orderCount = groups.Sum(group => group.Count);
        if (killSwitchOverride ?? agent.KillSwitch)
            violations.Add("The global trading kill switch is active.");
        if (orderCount > Math.Max(1, agent.MaxOrdersPerBatch))
            violations.Add($"Batch has {orderCount} orders; maximum is {agent.MaxOrdersPerBatch}.");

        if (agent.ExecutionUniverseSource == TradingExecutionUniverseSource.Watchlist
            && executionUniverseOverride is null)
        {
            violations.Add("The Watchlist execution universe was not resolved; execution fails closed.");
        }

        var universe = executionUniverseOverride ?? agent.AllowedSymbols;
        var allowed = universe
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liquidatable = (liquidationUniverseOverride ?? [])
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restrictToSelectedUniverse =
            agent.ExecutionUniverseSource == TradingExecutionUniverseSource.Watchlist
            || allowed.Count > 0;
        if (agent.RequireConfiguredSymbols && allowed.Count == 0)
            violations.Add("The selected execution universe is empty; execution fails closed.");

        decimal batchValue = 0;
        var index = 0;
        foreach (var order in groups.SelectMany(group => group))
        {
            index++;
            var label = $"Order {index}";
            var action = order.Action?.Trim().ToUpperInvariant();
            var symbol = order.Symbol?.Trim().ToUpperInvariant() ?? "";
            var orderType = order.OrderType?.Trim().ToUpperInvariant() ?? "LIMIT";

            if (action is not ("BUY" or "SELL"))
                violations.Add($"{label}: action must be BUY or SELL.");
            if (!SymbolPattern().IsMatch(symbol))
                violations.Add($"{label}: symbol '{symbol}' is invalid.");
            else if (restrictToSelectedUniverse
                     && !allowed.Contains(symbol)
                     && !(action == "SELL" && liquidatable.Contains(symbol)))
                violations.Add($"{label}: symbol '{symbol}' is not in the selected execution universe.");
            if (order.Quantity is not > 0)
                violations.Add($"{label}: quantity must be a positive integer.");
            if (orderType is not ("LIMIT" or "MARKET" or "STOPLOSS"))
                violations.Add($"{label}: order type must be LIMIT, MARKET, or STOPLOSS.");

            // A stop order is a trigger plus a limit, and the limit has to sit on the side the market
            // will be moving when the trigger fires: BELOW the trigger for a protective sell, ABOVE it
            // for a buy stop. The opposite asks for a better price than the move that triggered it,
            // which simply never fills — a stop that cannot fill is worse than no stop, because it
            // looks like protection.
            if (StopLimitRule.Validate(
                    action, orderType, order.FiresOnRisingPrice, order.EntryPrice, order.LimitPrice)
                is { } stopProblem)
            {
                violations.Add($"{label}: {stopProblem}");
            }

            if (orderType == "MARKET")
            {
                if (!cfg.AllowMarketOrders)
                    violations.Add($"{label}: market orders are disabled.");
            }
            else if (order.EntryPrice is not > 0)
            {
                violations.Add($"{label}: a positive limit price is required.");
            }
            // 0 (or less) means no cap: a hosted user sets it in Settings, and "unlimited" is a value they can type.
            else if (order.Quantity is > 0 && cfg.MaxOrderValuePkr > 0
                     && order.Quantity.Value * order.EntryPrice.Value > cfg.MaxOrderValuePkr)
            {
                violations.Add(
                    $"{label}: value exceeds MaxOrderValuePkr ({cfg.MaxOrderValuePkr:N0} PKR).");
            }

            if (order.Quantity is > 0 && order.EntryPrice is > 0)
                batchValue += order.Quantity.Value * order.EntryPrice.Value;
        }

        if (maxBatch > 0 && batchValue > maxBatch)
            violations.Add(
                $"Batch value {batchValue:N0} PKR exceeds MaxBatchValuePkr ({maxBatch:N0} PKR).");

        return violations.Count == 0
            ? RiskValidationResult.Allow()
            : new RiskValidationResult(false, violations);
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9.-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}
