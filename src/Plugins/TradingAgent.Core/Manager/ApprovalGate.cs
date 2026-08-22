using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Watchlist;

namespace TradingAgent.Manager;

/// <summary>
/// Decides whether an order may proceed WITHOUT a human confirming it, and expresses that decision the
/// same way a human approval is expressed: as a real, validated <see cref="ApprovalIntent"/>.
///
/// <para>
/// That is the important design choice. A pre-approval is not a bypass around
/// <see cref="TradingManager"/> — it goes through the identical
/// <c>ExecutionAuthorization → intent → hash re-check</c> path a clicked approval does, so a
/// pre-approved order is bound to the exact orders, policy version and expiry that were approved, and
/// a changed price or a replay is rejected in the same place. The only difference is who said yes.
/// </para>
///
/// <para>
/// It can never widen risk: the kill switch, AllowedSymbols, the market calendar, reconciliation
/// health, and the value caps all live in the risk engine and the manager, and none of them consult
/// this class. This decides confirmation only — and, in one direction only, refuses: a manual-only
/// symbol is denied here as early as possible, but the binding refusal is <see cref="TradingManager"/>'s.
/// </para>
/// </summary>
public sealed class ApprovalGate
{
    private readonly ApprovalIntentRegistry _intents;
    private readonly TradingPolicyProvider _policy;
    private readonly IMarketCalendar _calendar;
    private readonly TradingAgent.Market.OrderWindow _orderWindow;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<ApprovalGate> _logger;
    private readonly MonitoredUniverse? _universe;

    private readonly object _lock = new();
    private DateTime? _armedUntilUtc;
    private string? _armedBy;
    private DateOnly _sessionDate;
    private int _autoApprovedThisSession;

    public ApprovalGate(
        ApprovalIntentRegistry intents,
        TradingPolicyProvider policy,
        IMarketCalendar calendar,
        TradingAgent.Market.OrderWindow orderWindow,
        IOptions<TradingAgentOptions> options,
        ILogger<ApprovalGate> logger,
        MonitoredUniverse? universe = null)
    {
        _intents = intents;
        _policy = policy;
        _calendar = calendar;
        _orderWindow = orderWindow;
        _options = options;
        _logger = logger;
        // Optional so the gate still constructs in a test that does not care about the deny set. The
        // authoritative manual-only refusal is TradingManager's, not this early one.
        _universe = universe;
        _sessionDate = PsxTime.Today();
    }

    /// <summary>Current approval window, or null when prompting is in force.</summary>
    public (DateTime UntilUtc, string By)? ArmedWindow
    {
        get
        {
            lock (_lock)
            {
                if (_armedUntilUtc is null) return null;
                if (DateTime.UtcNow >= _armedUntilUtc) return null;
                // Arming does not survive the close: it was granted for a session someone was watching.
                if (_options.Value.Approval.Window.DisarmAtMarketClose && !_calendar.GetStatus().IsOpen)
                    return null;
                return (_armedUntilUtc.Value, _armedBy ?? "unknown");
            }
        }
    }

    /// <summary>
    /// Opens an approval window. Returns when it expires. Clamped to
    /// <see cref="ApprovalWindowOptions.MaxMinutes"/> — an unbounded window is indistinguishable from
    /// turning approval off, which is what <c>Mode</c> is for.
    /// </summary>
    public DateTime Arm(int? minutes, string actor)
    {
        var cfg = _options.Value.Approval.Window;
        var window = Math.Clamp(
            minutes ?? cfg.DefaultMinutes, 1, Math.Max(1, cfg.MaxMinutes));

        lock (_lock)
        {
            _armedUntilUtc = DateTime.UtcNow.AddMinutes(window);
            _armedBy = actor;
        }

        _logger.LogWarning(
            "[Approval] ARMED for {Minutes} minute(s) by {Actor}: orders will not ask for confirmation "
            + "until it expires, the kill switch is activated, or the market closes.", window, actor);

        return _armedUntilUtc!.Value;
    }

    /// <summary>Closes any approval window immediately.</summary>
    public void Disarm(string actor)
    {
        lock (_lock)
        {
            if (_armedUntilUtc is null) return;
            _armedUntilUtc = null;
            _armedBy = null;
        }
        _logger.LogWarning("[Approval] Disarmed by {Actor}; confirmation is required again.", actor);
    }

    /// <summary>
    /// Describes whether orders can currently fire unattended, without evaluating a specific order.
    ///
    /// <para>
    /// Exists so callers never re-derive this from configuration. The arm endpoint originally did, and
    /// promptly told the operator "this will NOT send" on a system running <c>BoundedAuto</c>, where it
    /// certainly would. One source of truth, asked rather than inferred.
    /// </para>
    /// </summary>
    public (bool WillFireUnattended, string Explanation) DescribeUnattendedPolicy()
    {
        var policy = _policy.Current();

        if (policy.KillSwitch)
            return (false, "The kill switch is active: nothing will be sent.");

        if (!policy.AutoExecute)
            return (false, "AutoExecute is off, so no order will be submitted regardless of approval mode.");

        var executionMode = policy.ExecutionMode.Trim();
        if (executionMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return (false, "Execution mode is Disabled.");

        if (executionMode.Equals("BoundedAuto", StringComparison.OrdinalIgnoreCase))
            return (true,
                "Execution mode is BoundedAuto: a triggered order WILL be submitted unattended, subject "
                + "to the risk engine's limits (AllowedSymbols, order value, market hours).");

        if (!executionMode.Equals("ApprovalRequired", StringComparison.OrdinalIgnoreCase))
            return (false, $"Execution mode is {executionMode}, which does not place live orders.");

        var mode = (_options.Value.Approval.Mode ?? "Always").Trim();
        if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return (true,
                "Approval mode is Auto: a triggered order will be submitted when it fits the configured "
                + "caps, and will otherwise wait for confirmation.");

        if (mode.Equals("Window", StringComparison.OrdinalIgnoreCase))
            return ArmedWindow is { } window
                ? (true, $"An approval window is open until {window.UntilUtc:HH:mm} UTC.")
                : (false, "Approval mode is Window and no window is open, so a trigger will wait.");

        return (false,
            "Approval mode is Always: a triggered order will stay armed and log that confirmation was "
            + "required. Set Approval.Mode to Auto, open a window, or use BoundedAuto execution.");
    }

    /// <summary>
    /// Decides whether an order may be submitted with nobody watching.
    ///
    /// <para>
    /// Three outcomes, not two. <b>NotRequired</b> means the execution mode itself already authorises
    /// unattended execution (<c>BoundedAuto</c>), so no intent is needed and demanding one would
    /// double-gate a mode whose entire meaning is "execute automatically within bounds".
    /// <b>Authorized</b> carries a minted intent for <c>ApprovalRequired</c> mode. <b>Denied</b> means a
    /// human has to confirm. The reason is always populated, because "why did it not fire" is the
    /// question this class exists to answer.
    /// </para>
    /// </summary>
    public ApprovalDecision Decide(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        ApprovalContext context)
    {
        var policy = _policy.Current();
        var approval = _options.Value.Approval;

        // The kill switch outranks every approval rule. Checked here as well as in the risk engine so
        // an open window cannot even mint an authorization while it is active.
        if (policy.KillSwitch)
        {
            Disarm("kill-switch");
            return ApprovalDecision.Denied("The kill switch is active.");
        }

        // A manual-only symbol outranks the execution mode, and is therefore checked BEFORE the
        // BoundedAuto exit below — which returns "no approval needed" and would otherwise wave through
        // the one mode where an unattended order is most likely. See TradingAgentOptions.ManualOnlySymbols.
        //
        // Best-effort by necessity: this method is synchronous and the watchlist half of the deny set
        // lives in the database. A symbol switched to manual-only seconds ago can still be missed here
        // and is then refused at the execution boundary instead, which is the authoritative check.
        if (_universe?.FirstManualOnlySnapshot(
                groups.SelectMany(g => g).Select(o => o.Symbol)) is { } manualSymbol)
        {
            return ApprovalDecision.Denied(
                $"{manualSymbol} is set to manual-only: unattended execution is not available for it, "
                + "so this order waits for you to place it by hand.");
        }

        // BoundedAuto is itself the operator saying "act within the configured bounds". TradingManager
        // requires no authorization in that mode, so requiring one here would mean an armed trigger
        // silently never fires on a system explicitly configured for automatic execution.
        var executionMode = policy.ExecutionMode.Trim();
        if (executionMode.Equals("BoundedAuto", StringComparison.OrdinalIgnoreCase))
        {
            return ApprovalDecision.NotRequired(
                "Execution mode is BoundedAuto: unattended execution is authorised by the mode itself, "
                + "within the risk engine's limits.");
        }

        var mode = (approval.Mode ?? "Always").Trim();

        if (mode.Equals("Window", StringComparison.OrdinalIgnoreCase))
        {
            if (ArmedWindow is { } window)
            {
                return ApprovalDecision.Authorized(
                    Mint(groups, sourceMessage, policy.Version, $"approval-window:{window.By}"),
                    $"Approval window open until {window.UntilUtc:HH:mm} UTC (opened by {window.By}).");
            }

            return ApprovalDecision.Denied("Approval mode is Window but no window is open.");
        }

        if (!mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return ApprovalDecision.Denied(
                "Approval mode is Always: a human must confirm this order.");
        }

        // ── Auto: every cap must hold ─────────────────────────────────────────
        var caps = approval.Auto;
        RollSessionIfNeeded();

        // Asks the same question the order gate asks, so auto-approval and execution cannot disagree.
        // Gating on the regular session alone would deny auto-approval during the pre-open OHO state
        // that TradingManager is willing to submit into — approving nothing that could then be placed.
        if (caps.RequireMarketOpen)
        {
            var window = _orderWindow.Evaluate();
            if (!window.Allowed)
                return ApprovalDecision.Denied($"Auto-approval requires a market accepting orders: {window.Reason}");
        }

        var orders = groups.SelectMany(g => g).ToList();
        if (orders.Count == 0)
        {
            return ApprovalDecision.Denied("No orders to authorize.");
        }

        foreach (var order in orders)
        {
            var side = order.Action?.Trim().ToUpperInvariant() ?? "";
            if (!caps.Sides.Any(s => s.Trim().Equals(side, StringComparison.OrdinalIgnoreCase)))
            {
                return ApprovalDecision.Denied(
                    $"Auto-approval does not cover {side} orders "
                    + $"(allowed: {string.Join(", ", caps.Sides)}).");
            }

            if (caps.Symbols.Count > 0
                && !caps.Symbols.Any(s => s.Trim().Equals(order.Symbol, StringComparison.OrdinalIgnoreCase)))
            {
                return ApprovalDecision.Denied(
                    $"{order.Symbol} is not in the auto-approval symbol list.");
            }

            var value = (order.EntryPrice ?? 0m) * (order.Quantity ?? 0);
            if (value > caps.MaxOrderValuePkr)
            {
                return ApprovalDecision.Denied(
                    $"Order value {value:N0} PKR exceeds the auto-approval cap "
                    + $"({caps.MaxOrderValuePkr:N0} PKR).");
            }
        }

        if (!string.IsNullOrWhiteSpace(caps.MinAlertSeverity))
        {
            if (context.AlertSeverity is null)
            {
                return ApprovalDecision.Denied(
                    $"Auto-approval requires an alert of at least {caps.MinAlertSeverity} severity; "
                    + "this order has no alert behind it.");
            }

            if (Rank(context.AlertSeverity) < Rank(caps.MinAlertSeverity))
            {
                return ApprovalDecision.Denied(
                    $"Alert severity {context.AlertSeverity} is below the required "
                    + $"{caps.MinAlertSeverity}.");
            }
        }

        lock (_lock)
        {
            if (_autoApprovedThisSession + orders.Count > Math.Max(1, caps.MaxOrdersPerSession))
            {
                return ApprovalDecision.Denied(
                    $"Auto-approval session cap reached "
                    + $"({_autoApprovedThisSession}/{caps.MaxOrdersPerSession} used).");
            }
            _autoApprovedThisSession += orders.Count;
        }

        return ApprovalDecision.Authorized(
            Mint(groups, sourceMessage, policy.Version, "approval-auto"),
            $"Auto-approved within configured caps "
            + $"(severity {context.AlertSeverity ?? "n/a"}, {orders.Count} order(s)).");
    }

    /// <summary>Auto-approvals used this session, for the status endpoint.</summary>
    public int AutoApprovedThisSession
    {
        get { RollSessionIfNeeded(); lock (_lock) return _autoApprovedThisSession; }
    }

    private ExecutionAuthorization Mint(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        string policyVersion,
        string actor)
    {
        // A real intent, registered and single-use, exactly as a clicked approval produces. The manager
        // re-derives its hash before submission, so a price that moved between minting and submitting
        // is rejected there rather than silently traded.
        var intent = ApprovalIntent.Create(
            groups, sourceMessage, policyVersion,
            TimeSpan.FromSeconds(Math.Max(10, _options.Value.ApprovalIntentTtlSeconds)));

        _intents.Register(intent);
        _logger.LogWarning(
            "[Approval] Pre-authorized by {Actor}: intent {IntentId}, exposure {Exposure:N0} PKR. "
            + "This order will NOT ask for confirmation.",
            actor, intent.IntentId, intent.EstimatedExposurePkr);

        // PreAuthorized, not HostToolGate: identical gate and identical intent, but nobody was watching.
        // TradingManager needs that distinction to keep automation out of a manual-only symbol.
        return ExecutionAuthorization.PreAuthorized(actor, intent);
    }

    /// <summary>The per-session counter resets with the trading day, not with the process.</summary>
    private void RollSessionIfNeeded()
    {
        var today = PsxTime.Today();
        lock (_lock)
        {
            if (_sessionDate == today) return;
            _sessionDate = today;
            _autoApprovedThisSession = 0;
        }
    }

    private static int Rank(string severity) => severity.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" => 4,
        "HIGH" => 3,
        "MEDIUM" => 2,
        "LOW" => 1,
        _ => 0
    };
}

/// <summary>What is known about why an order is being placed, for the auto-approval caps.</summary>
public sealed record ApprovalContext(string? AlertSeverity = null, string? Source = null);

/// <summary>
/// Whether an order may be submitted unattended, and why.
///
/// <para>
/// The distinction between <see cref="NotRequired"/> and <see cref="Authorized"/> matters: the first
/// means the execution mode already permits unattended execution and no intent is needed; the second
/// carries a minted intent for a mode that demands one. Collapsing them into a nullable authorization
/// is what made an armed trigger refuse to fire on a system configured for automatic execution.
/// </para>
/// </summary>
public sealed record ApprovalDecision(bool MayProceed, ExecutionAuthorization? Authorization, string Reason)
{
    public static ApprovalDecision NotRequired(string reason) => new(true, null, reason);

    public static ApprovalDecision Authorized(ExecutionAuthorization authorization, string reason) =>
        new(true, authorization, reason);

    public static ApprovalDecision Denied(string reason) => new(false, null, reason);
}
