using System.Globalization;
using System.Text.Json;
using AgentFox.Plugins;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Research;

namespace TradingAgent.Watchlist;

/// <summary>
/// Runs the exit rules of plan step 10 for every HELD symbol that has a position plan opting in.
///
/// <list type="bullet">
/// <item>After a session settles: closing-price stop, time stop, relative strength — on the settled close.</item>
/// <item>Inside a session: the gap rule, once per symbol per session, on its first price.</item>
/// </list>
///
/// <para>
/// <b>Proposal-only by default</b> (<see cref="TradingAgentOptions.ExitRulesAction"/>, owner 2026-11-28): a rule that fires
/// writes a trade proposal and alerts; the sale happens when a person approves it. Opted in to Act, it arms a scheduled
/// SELL instead, which in ApprovalRequired still waits at the gate. Every output is keyed by rule, symbol and date, so a
/// restart or a second pass never proposes or arms the same exit twice.
/// </para>
/// </summary>
public sealed class ExitRuleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMarketCalendar _calendar;
    private readonly TradingReconciliationState _reconciliation;
    private readonly PsxDataClient _psx;
    private readonly CompositeLiveQuoteSource _quotes;
    private readonly PluginConfigManager _configs;
    private readonly TradingPolicyProvider _policy;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<ExitRuleWorker> _logger;
    private readonly IUserNotifier? _notifier;
    private readonly HashSet<string> _done = [];

    public ExitRuleWorker(IServiceScopeFactory scopes, IMarketCalendar calendar, TradingReconciliationState reconciliation,
        PsxDataClient psx, CompositeLiveQuoteSource quotes, PluginConfigManager configs, TradingPolicyProvider policy,
        IOptions<TradingAgentOptions> options, ILogger<ExitRuleWorker> logger, IUserNotifier? notifier = null)
    {
        _scopes = scopes; _calendar = calendar; _reconciliation = reconciliation; _psx = psx; _quotes = quotes;
        _configs = configs; _policy = policy; _options = options; _logger = logger; _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "[ExitRules] Pass failed; trying again next minute."); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The configured action, Settings first.</summary>
    internal ExitRuleAction Action()
    {
        var runtime = _configs.GetConfig("trading-agent");
        return runtime.TryGetValue("exitRulesAction", out var raw)
               && Enum.TryParse<ExitRuleAction>(raw?.ToString(), ignoreCase: true, out var chosen)
            ? chosen
            : _options.Value.ExitRulesAction;
    }

    /// <summary>One pass. Internal for tests; returns the exits it proposed or armed.</summary>
    internal async Task<IReadOnlyList<ExitCandidate>> RunOnceAsync(CancellationToken ct = default)
    {
        var action = Action();
        if (action == ExitRuleAction.Off) return [];

        var snapshot = _reconciliation.Current;
        if (!snapshot.Supported || !snapshot.Healthy) return [];
        var held = snapshot.Positions.Where(p => p.Quantity > 0)
            .ToDictionary(p => p.Symbol, p => (int)Math.Floor(p.Quantity), StringComparer.OrdinalIgnoreCase);
        if (held.Count == 0) return [];

        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITradingRepository>();
        if (repository is not IPositionPlanStore store) return [];
        var plans = (await store.GetPositionPlansAsync(ct)).Where(p => held.ContainsKey(p.Symbol)).ToList();
        if (plans.Count == 0) return [];

        var status = _calendar.GetStatus();
        var today = DateOnly.FromDateTime(status.PktNow);
        var fired = new List<(ExitCandidate Candidate, decimal Reference, string Key)>();

        if (status.IsOpen)
        {
            var stops = await repository.GetProtectiveStopsAsync(openOnly: true, ct);
            var live = (await _quotes.GetQuotesAsync(ct)).Quotes;
            foreach (var plan in plans.Where(p => p.GapExit))
            {
                var key = $"gap:{plan.Symbol}:{today:yyyyMMdd}";
                if (_done.Contains(key)) continue;
                if (!live.TryGetValue(plan.Symbol, out var q) || q.Current is not > 0) continue;
                _done.Add(key);   // the session's first price decides; later prices do not re-open the question
                var trigger = stops.Where(s => s.State == "active" && s.Symbol.Equals(plan.Symbol, StringComparison.OrdinalIgnoreCase))
                    .Select(s => (decimal?)s.StopTrigger).Max();
                if (trigger is { } t && ExitRules.Gap(plan, t, q.Current!.Value) is { } gap)
                    fired.Add((gap, q.Current.Value, key));
            }
        }
        else
        {
            var index = plans.Any(p => p.RelativeStrengthExit) ? await _psx.GetEodClosesAsync("KSE100", 45, ct) : [];
            foreach (var plan in plans)
            {
                var closes = await _psx.GetEodClosesAsync(plan.Symbol, 260, ct);
                if (closes.Count == 0) continue;
                var settled = closes[^1];
                var key = $"{plan.Symbol}:{settled.Date:yyyyMMdd}";
                if (!_done.Add(key)) continue;

                if (ExitRules.ClosingPrice(plan, settled) is { } closing) fired.Add((closing, settled.Close, "closing:" + key));
                if (ExitRules.RelativeStrength(plan, closes, index) is { } rs) fired.Add((rs, settled.Close, "rs:" + key));
                if (ExitRules.TimeStop(plan, settled.Date, closes) is { } time)
                {
                    if (time.Exit) fired.Add((time.Candidate, settled.Close, "time:" + key));
                    else await NotifyAsync($"⏳ **Time stop — {plan.Symbol}**\n• {time.Candidate.Reason}\n"
                        + "_Flag only: nothing is proposed. Set the time stop to exit in the position plan to have one proposed._");
                }
            }
        }

        foreach (var (candidate, reference, key) in fired)
            await ApplyAsync(repository, candidate, held[candidate.Symbol], reference, key, action, ct);
        return fired.Select(f => f.Candidate).ToList();
    }

    private async Task ApplyAsync(ITradingRepository repository, ExitCandidate exit, int quantity, decimal reference,
        string key, ExitRuleAction action, CancellationToken ct)
    {
        var limit = Math.Round(reference * (1m - _options.Value.StopLimitOffsetPct / 100m), 2, MidpointRounding.AwayFromZero);
        if (action == ExitRuleAction.Propose)
        {
            var json = JsonSerializer.Serialize(new
            {
                orders = new[] { new { action = "SELL", symbol = exit.Symbol, quantity, price = limit, order_type = "LIMIT" } },
                source_message = $"exit-rule:{key}",
                rationale = $"{exit.Rule}: {exit.Reason}"
            });
            var id = await repository.CreateProposalAsync($"exit-rule:{key}", json, _policy.Current().Version, ct);
            _logger.LogWarning("[ExitRules] Proposed selling {Qty} {Symbol} ({Rule}) as {Proposal}: {Why}",
                quantity, exit.Symbol, exit.Rule, id, exit.Reason);
            await NotifyAsync($"📤 **Exit proposed — {exit.Symbol}**\n• {exit.Reason}\n"
                + $"• SELL {quantity:N0} at {limit:0.##}{(exit.AtNextOpen ? " at the next open" : "")}\n"
                + "_Nothing is sold until you approve the proposal._");
            return;
        }

        var armedId = $"exit-{key.Replace(':', '-')}";
        if ((await repository.GetArmedOrdersAsync(armedOnly: false, ct)).Any(o => o.ArmedId == armedId)) return;
        await repository.SaveArmedOrderAsync(new ArmedOrder
        {
            ArmedId = armedId,
            Symbol = exit.Symbol,
            TriggerKind = ArmedTriggerKind.Scheduled,
            // Scheduled for now: the monitor holds it while the market is closed and fires it at the open.
            ActiveFromUtc = DateTime.UtcNow,
            Action = "SELL",
            Quantity = quantity,
            OrderType = "LIMIT",
            Price = limit,
            Note = $"Exit rule {exit.Rule}: {exit.Reason}"
        }, ct);
        _logger.LogWarning("[ExitRules] Armed SELL {Qty} {Symbol} at {Limit} ({Rule}): {Why}",
            quantity, exit.Symbol, limit, exit.Rule, exit.Reason);
        await NotifyAsync($"📤 **Exit armed — {exit.Symbol}**\n• {exit.Reason}\n• SELL {quantity:N0} at {limit:0.##}");
    }

    private async Task NotifyAsync(string message)
    {
        if (_notifier is null) return;
        try { await _notifier.NotifyAsync(message, TradingTopics.ExitRule); }
        catch (Exception ex) { _logger.LogWarning(ex, "[ExitRules] Could not deliver the alert."); }
    }
}
