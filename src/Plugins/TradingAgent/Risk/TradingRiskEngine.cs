using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;

namespace TradingAgent.Risk;

/// <summary>Deterministic pre-trade validation that cannot be bypassed by agent prompting.</summary>
public sealed partial class TradingRiskEngine : ITradingRiskEngine
{
    private readonly IOptions<AhkConfig> _options;
    private readonly IOptions<TradingAgentOptions> _agentOptions;

    public TradingRiskEngine(
        IOptions<AhkConfig> options,
        IOptions<TradingAgentOptions> agentOptions)
    {
        _options = options;
        _agentOptions = agentOptions;
    }

    public RiskValidationResult Validate(IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
    {
        var cfg = _options.Value;
        var agent = _agentOptions.Value;
        var violations = new List<string>();
        var orderCount = groups.Sum(group => group.Count);
        if (agent.KillSwitch)
            violations.Add("The global trading kill switch is active.");
        if (orderCount > Math.Max(1, agent.MaxOrdersPerBatch))
            violations.Add($"Batch has {orderCount} orders; maximum is {agent.MaxOrdersPerBatch}.");

        var allowed = agent.AllowedSymbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (agent.RequireConfiguredSymbols && allowed.Count == 0)
            violations.Add("No AllowedSymbols are configured; execution fails closed.");

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
            else if (allowed.Count > 0 && !allowed.Contains(symbol))
                violations.Add($"{label}: symbol '{symbol}' is not in AllowedSymbols.");
            if (order.Quantity is not > 0)
                violations.Add($"{label}: quantity must be a positive integer.");
            if (orderType is not ("LIMIT" or "MARKET"))
                violations.Add($"{label}: order type must be LIMIT or MARKET.");

            if (orderType == "MARKET")
            {
                if (!cfg.AllowMarketOrders)
                    violations.Add($"{label}: market orders are disabled.");
            }
            else if (order.EntryPrice is not > 0)
            {
                violations.Add($"{label}: a positive limit price is required.");
            }
            else if (order.Quantity is > 0
                     && order.Quantity.Value * order.EntryPrice.Value > cfg.MaxOrderValuePkr)
            {
                violations.Add(
                    $"{label}: value exceeds MaxOrderValuePkr ({cfg.MaxOrderValuePkr:N0} PKR).");
            }

            if (order.Quantity is > 0 && order.EntryPrice is > 0)
                batchValue += order.Quantity.Value * order.EntryPrice.Value;
        }

        if (batchValue > agent.MaxBatchValuePkr)
            violations.Add(
                $"Batch value {batchValue:N0} PKR exceeds MaxBatchValuePkr ({agent.MaxBatchValuePkr:N0} PKR).");

        return violations.Count == 0
            ? RiskValidationResult.Allow()
            : new RiskValidationResult(false, violations);
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9.-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}
