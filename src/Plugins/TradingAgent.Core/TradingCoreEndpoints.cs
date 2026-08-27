using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using AgentFox.Plugins;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TradingAgent.AhlAnalytics;
using TradingAgent.Analysis;
using TradingAgent.Broker;
using TradingAgent.Models;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Research;
using TradingAgent.Risk;
using TradingAgent.Reconciliation;
using TradingAgent.Safety;
using TradingAgent.Tools;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace TradingAgent;

/// <summary>
/// The <c>/trading</c> management API. Split out of the entry module so every edition serves the
/// same endpoints from the same code — an edition adds routes, it does not re-map these.
///
/// <para>
/// Mapping one of these routes a second time does not override it: two endpoints with the same
/// template, method, and precedence make the request ambiguous and routing throws at request time.
/// Premium behavior therefore arrives through dependency injection (a provider a handler already
/// consults), not by shadowing a route.
/// </para>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    /// <summary>Not instantiable — every member is static. The type exists as a type so it can
    /// serve as this code's <c>ILogger&lt;T&gt;</c> category, which a static class cannot do.</summary>
    private TradingCoreEndpoints() { }


    public static void MapCoreEndpoints(IEndpointRouteBuilder endpoints)
    {
        var trading = endpoints.MapGroup("/trading")
            .RequireAuthorization("ManagementViewer");

        // One call per area; each lives in an Endpoints.<Area>.cs partial. Order is irrelevant to
        // routing (templates are matched by precedence) and is kept alphabetical-by-concern for
        // readability only.
        MapStatusEndpoints(trading);
        MapOrdersEndpoints(trading);
        MapCandleArchiveEndpoints(trading);
        MapArmedOrdersEndpoints(trading);
        MapMarketEndpoints(trading);
        MapAlertsEndpoints(trading);
        MapAssessmentEndpoints(trading);
        MapChartsEndpoints(trading);
        MapWatchlistEndpoints(trading);
        MapProposalsEndpoints(trading);
        MapLedgerEndpoints(trading);
    }
}


public sealed record KillSwitchRequest(bool Active, string? Reason = null);

/// <summary>A ticker to add to the monitoring watchlist.</summary>
public sealed record WatchlistSymbolRequest(string? Symbol);

/// <summary>An ad-hoc assessment request from the chart pane.</summary>
public sealed record AssessRequest(string? Symbol, string? Interval = null, string? Context = null);

/// <summary>Why a proposal was rejected — recorded so a terminal state is explicable later.</summary>
public sealed record ProposalRejectRequest(string? Reason = null);

public sealed record ResolveUnknownExecutionRequest(string? Resolution, string? Note);

/// <summary>
/// Operator resolution for a persistent order stuck in "attention" from a prior trading date.
/// <paramref name="FilledQuantity"/> is required only when <paramref name="Resolution"/> is "partial".
/// </summary>
public sealed record ResolvePersistentAttentionRequest(
    string? Resolution, int? FilledQuantity, string? Note);

/// <summary>An order to hold until a price level is reached or an alert kind fires.</summary>
/// <param name="TriggerPercent">
/// Size of the move, in percent, for a PercentDrop/PercentRise trigger. Ignored by every other kind.
/// </param>
/// <param name="ReferencePrice">
/// The price a percent trigger measures its move from. Send the price the operator was looking at, so
/// the level armed is the level they were quoted; omitted, it is captured from the live feed.
/// </param>
/// <param name="Trailing">
/// Percent triggers only. The reference follows the price in the favourable direction — the high for a
/// drop trigger, the low for a rise — making a drop trigger a trailing stop. Never moves back.
/// </param>
public sealed record ArmOrderRequest(
    string? Symbol,
    string? Action,
    int? Quantity,
    string? TriggerKind,
    decimal? TriggerPrice = null,
    string? TriggerAlertKind = null,
    string? OrderType = "LIMIT",
    decimal? Price = null,
    decimal? LimitPrice = null,
    DateTime? ExpiresUtc = null,
    int? ExpiresInDays = null,
    string? Note = null,
    string? SourceAlertId = null,
    AttachStopRequest? AttachStop = null,
    decimal? TriggerPercent = null,
    decimal? ReferencePrice = null,
    bool Trailing = false,
    bool PersistentUntilFilled = false);

/// <summary>An immediate order submitted from a registry choice in the trading dashboard.</summary>
public sealed record DashboardOrderRequest(
    string? OrderIntentId,
    string? Symbol,
    int? Quantity,
    decimal? Price = null,
    decimal? TriggerPrice = null,
    decimal? LimitPrice = null,
    string? ClientRequestId = null,
    bool PersistentUntilFilled = false,
    DateTime? ExpiresUtc = null,
    int? ExpiresInDays = null);

/// <summary>Auditable bulk alert state change. Dismiss is the UI's soft-delete operation.</summary>
public sealed record BulkAlertActionRequest(
    string? Action,
    IReadOnlyList<string>? AlertIds = null,
    bool All = false);

/// <summary>
/// A protective stop to attach to a BUY entry, armed only once the entry is confirmed filled.
/// </summary>
/// <param name="Quantity">
/// Shares to protect. Null follows the entry's own quantity, clamped to what actually fills.
/// </param>
/// <param name="Recurring">
/// Re-place the native stop every session. On by default, because this venue clears outstanding
/// orders at the close — a one-shot stop protects the position for a single day and then lapses
/// silently.
/// </param>
public sealed record AttachStopRequest(
    decimal? StopTrigger,
    decimal? StopLimit = null,
    int? Quantity = null,
    bool Recurring = true);

/// <summary>How long to suspend order confirmation for.</summary>
public sealed record ArmApprovalRequest(int? Minutes = null);

/// <summary>Per-symbol watchlist fields the user controls. Null means "leave unchanged".</summary>
/// <param name="AutoTradeEnabled">
/// False makes the symbol manual-only — no automation may originate an order for it, entry or exit,
/// while the operator still can. Setting it true does not lift a pin from
/// <c>Plugins:TradingAgent:ManualOnlySymbols</c>; config is the floor the API cannot raise.
/// </param>
public sealed record WatchlistUpdateRequest(
    bool? AlertsEnabled = null,
    string? Notes = null,
    bool? Pinned = null,
    bool? AutoTradeEnabled = null);

/// <summary>Sets the runtime automation preference for every watched symbol.</summary>
public sealed record WatchlistAutomationRequest(bool? AutoTradeEnabled = null);

public sealed record WatchlistReorderRequest(IReadOnlyList<string>? Symbols);

/// <summary>How an official index universe should be applied to the monitoring watchlist.</summary>
public sealed record WatchlistPresetRequest(string? Mode);

/// <summary>Optional depth override for a manually triggered backfill; null uses the configured years.</summary>
/// <summary>
/// A backfill trigger. <paramref name="Symbols"/> scopes which dates count as missing — the dates those
/// symbols were never requested for — not which symbols are stored; a session fetch returns the whole
/// market regardless. Null or empty means every archived symbol.
/// </summary>
public sealed record CandleBackfillRequest(int? Years = null, IReadOnlyList<string>? Symbols = null);
