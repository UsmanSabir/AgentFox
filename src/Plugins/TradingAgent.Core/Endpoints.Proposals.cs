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
/// <c>/trading</c> endpoints for specialist-authored proposals and their execution.
///
/// <para>
/// One area of the management API. These were a single 1,855-line MapEndpoints method; the
/// split is by area so a route change is reviewable and so an edition adding endpoints does
/// not collide with core edits. Registration order across areas does not matter — endpoint
/// routing matches on template precedence, not on the order routes were mapped.
/// </para>
///
/// <para>Routes here:</para>
/// <list type="bullet">
///   <item><description><c>/proposals</c></description></item>
///   <item><description><c>/proposals/{proposalId}/execute</c></description></item>
///   <item><description><c>/proposals/{proposalId}/reject</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapProposalsEndpoints(RouteGroupBuilder trading)
    {
        // ── Proposals: the signal inbox ────────────────────────────────────────
        // A proposal is what the specialist produced from a signal that arrived while nobody was
        // watching (a WhatsApp tip overnight). It used to be write-only — created, listed, never
        // resolved — so the table only grew. It now has a lifecycle:
        //   proposed → executing → executed | rejected | expired

        trading.MapGet("/proposals", async (
            int? limit,
            bool? openOnly,
            ITradingRepository repository,
            CancellationToken ct) =>
            // Open-only by default: an empty inbox is the normal state, and a list dominated by
            // last month's resolved proposals is what made this feel like a log.
            Results.Ok(openOnly ?? true
                ? await repository.GetOpenProposalsAsync(ct)
                : await repository.GetProposalsAsync(limit ?? 100, ct)));

        trading.MapPost("/proposals/{proposalId}/execute", async (
            string proposalId,
            ITradingRepository repository,
            TradingAgent.Manager.TradingManager manager,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var proposal = await repository.GetProposalAsync(proposalId, ct);
            if (proposal is null)
                return Results.NotFound(new { error = "unknown_proposal", proposalId });

            if (proposal.Status is "executed" or "rejected" or "expired")
                return Results.Conflict(new
                {
                    error = "already_resolved",
                    proposalId,
                    status = proposal.Status,
                    message = $"This proposal is already {proposal.Status}"
                            + (proposal.StateReason is { } r ? $": {r}" : ".")
                });

            // Claim it before touching the broker. The compare-and-set is what stops a double click
            // from submitting the same orders twice — whoever loses the race gets the conflict below
            // rather than a second live order.
            if (!await repository.TrySetProposalStateAsync(
                    proposalId, proposal.Status, "executing", ct: ct))
                return Results.Conflict(new
                {
                    error = "already_claimed",
                    proposalId,
                    message = "Another request is already executing this proposal."
                });

            var orders = ParseProposalOrders(proposal.Proposal);
            if (orders.Count == 0)
            {
                await repository.TrySetProposalStateAsync(
                    proposalId, "executing", "rejected",
                    "The proposal contains no executable orders.", ct: ct);
                return Results.BadRequest(new
                {
                    error = "no_orders",
                    message = "The proposal contains no orders that could be executed."
                });
            }

            try
            {
                // Straight through the deterministic manager: policy, calendar, risk engine, kill
                // switch, idempotency and audit all still apply. This endpoint adds no execution path,
                // it only supplies the orders a human approved.
                // Each order as its own group: they are independent, so one failing must not skip the
                // rest (grouping means "stop at the first failure", which is for a buy→sell pair).
                var groups = orders.Select(o => (IReadOnlyList<TradingSignal>)[o]).ToList();
                var result = await manager.ExecuteGroupsAsync(
                    groups, $"proposal:{proposalId}", ct: ct);

                await repository.TrySetProposalStateAsync(
                    proposalId, "executing",
                    result.Executed ? "executed" : "proposed",
                    result.Executed ? null : $"Execution refused: {result.Reason}",
                    string.IsNullOrWhiteSpace(result.ExecutionId) ? null : result.ExecutionId, ct);

                logger.LogInformation(
                    "[Trading] Proposal {ProposalId} execution {Outcome} (execution {ExecutionId}).",
                    proposalId, result.Executed ? "accepted" : "refused", result.ExecutionId);

                return Results.Ok(new
                {
                    proposalId,
                    // Refused returns to 'proposed' deliberately: the reason is usually transient
                    // (market closed, reconciliation stale, approval required), so the proposal stays
                    // actionable rather than being burned by a failed attempt.
                    status = result.Executed ? "executed" : "proposed",
                    accepted = result.Executed,
                    isReplay = result.IsReplay,
                    executionId = result.ExecutionId,
                    reason = result.Reason
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await repository.TrySetProposalStateAsync(
                    proposalId, "executing", "proposed", $"Execution failed: {ex.Message}", ct: ct);
                logger.LogError(ex, "[Trading] Proposal {ProposalId} execution failed.", proposalId);
                return Results.Problem(title: "execution_failed", detail: ex.Message, statusCode: 502);
            }
        }).RequireAuthorization("TradingTrader");

        trading.MapPost("/proposals/{proposalId}/reject", async (
            string proposalId,
            ProposalRejectRequest? body,
            ITradingRepository repository,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var proposal = await repository.GetProposalAsync(proposalId, ct);
            if (proposal is null)
                return Results.NotFound(new { error = "unknown_proposal", proposalId });

            var moved = await repository.TrySetProposalStateAsync(
                proposalId, proposal.Status, "rejected",
                body?.Reason ?? "Rejected by the operator.", ct: ct);

            if (!moved)
                return Results.Conflict(new { error = "already_resolved", status = proposal.Status });

            logger.LogInformation("[Trading] Proposal {ProposalId} rejected: {Reason}",
                proposalId, body?.Reason ?? "(no reason given)");
            return Results.Ok(new { proposalId, status = "rejected" });
        }).RequireAuthorization("TradingAnalyst");

    }

    /// <summary>
    /// Reads the executable orders out of a stored proposal.
    ///
    /// <para>
    /// The proposal JSON is authored by the specialist, so this is deliberately forgiving about shape
    /// but strict about substance: anything without a symbol and a BUY/SELL action is skipped rather
    /// than guessed at. Nothing here bypasses validation — the risk engine still re-checks every field
    /// before an order reaches the broker.
    /// </para>
    /// </summary>
    private static List<TradingSignal> ParseProposalOrders(JsonElement proposal)
    {
        var orders = new List<TradingSignal>();
        if (!proposal.TryGetProperty("orders", out var array) || array.ValueKind != JsonValueKind.Array)
            return orders;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string? Text(params string[] names)
            {
                foreach (var n in names)
                {
                    if (item.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                }
                return null;
            }

            decimal? Number(params string[] names)
            {
                foreach (var n in names)
                {
                    if (!item.TryGetProperty(n, out var v)) continue;
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                    if (v.ValueKind == JsonValueKind.String
                        && decimal.TryParse(v.GetString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var parsed)) return parsed;
                }
                return null;
            }

            var action = Text("action", "side")?.Trim().ToUpperInvariant();
            var symbol = Text("symbol", "scrip")?.Trim().ToUpperInvariant();
            if (action is not ("BUY" or "SELL") || string.IsNullOrWhiteSpace(symbol)) continue;

            orders.Add(new TradingSignal
            {
                IsSignal   = true,
                Action     = action,
                Symbol     = symbol,
                Quantity   = (int?)Number("quantity", "qty", "volume"),
                EntryPrice = Number("entry_price", "entryPrice", "price", "trigger"),
                LimitPrice = Number("limit_price", "limitPrice"),
                Target     = Number("target", "take_profit"),
                StopLoss   = Number("stop_loss", "stopLoss", "stop"),
                OrderType  = (Text("order_type", "orderType") ?? "LIMIT").Trim().ToUpperInvariant()
            });
        }

        return orders;
    }
}
