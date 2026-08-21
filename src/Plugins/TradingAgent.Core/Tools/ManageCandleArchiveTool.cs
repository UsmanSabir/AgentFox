using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Trading;

namespace TradingAgent.Tools;

/// <summary>
/// Reports and triggers the daily-candle backfill.
///
/// Exists because the archive's depth is the limiting factor on weekly levels: without enough stored
/// history the multi-timeframe analysis reports <c>unknown</c> alignment, and the honest answer to
/// "why are there no weekly levels" is "the archive is still filling". This makes that state
/// inspectable and fixable from the conversation instead of only from the web UI or the logs.
///
/// A backfill pass is long (about 18 minutes for two years) so <c>backfill</c> starts it and returns
/// immediately; call <c>status</c> again to follow progress. It never blocks a turn on the full run.
/// </summary>
public sealed class ManageCandleArchiveTool : BaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CandleBackfillRunner _runner;
    private readonly ILogger<ManageCandleArchiveTool> _logger;

    public ManageCandleArchiveTool(CandleBackfillRunner runner, ILogger<ManageCandleArchiveTool> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public override string Name => "manage_candle_archive";

    public override string Description =>
        "Check or extend the local archive of daily PSX candles that support/resistance and WEEKLY " +
        "levels are computed from. Use 'status' to report how many sessions and symbols are stored, how " +
        "many trading days are still missing, and any backfill in progress — this is the answer to " +
        "'why is there no weekly data' or 'how far back does the history go'. Use 'backfill' to start " +
        "fetching the missing days; it runs in the background (roughly 18 minutes for two years) and " +
        "returns straight away, so report that it started and check status later rather than waiting. " +
        "If 'status' lists symbols_short_of_weekly, backfill THOSE symbols by name: coverage is tracked " +
        "per symbol and date, so a symbol added after the deep history was archived is missing those " +
        "dates even though missing_trading_days looks small — an unscoped pass will not reach it.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"] = new()
        {
            Type = "string",
            Description = "'status' to report archive coverage and backfill progress, " +
                          "'backfill' to start filling the missing trading days in the background.",
            EnumValues = ["status", "backfill"],
            Required = true
        },
        ["years"] = new()
        {
            Type = "integer",
            Description = "Backfill only: how many years back to archive (1-15). Omit to use the " +
                          "configured Scan.BackfillYears. Deeper history means a longer run.",
            Required = false
        },
        ["symbols"] = new()
        {
            Type = "string",
            Description = "Backfill only: comma-separated symbols to target, e.g. 'GHNI,GAL'. Scopes " +
                          "which DATES the pass treats as missing — the ones those symbols were never " +
                          "requested for — not which symbols get stored; each day fetched stores the " +
                          "whole archived universe anyway. Use this to fill a symbol reported in " +
                          "symbols_short_of_weekly. Omit to cover every archived symbol. Symbols must " +
                          "already be on the watchlist or in AllowedSymbols.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var action = (ToolArgs.Text(arguments, "action") ?? "").Trim().ToLowerInvariant();

        if (action is not ("status" or "backfill"))
            return ToolResult.Fail("Parameter 'action' must be 'status' or 'backfill'.");

        try
        {
            if (action == "status")
                return ToolResult.Ok(JsonSerializer.Serialize(
                    await _runner.GetStatusAsync(), JsonOptions));

            var years = ToolArgs.Int(arguments, "years");
            if (years is not null && (years < 1 || years > 15))
                return ToolResult.Fail("Parameter 'years' must be between 1 and 15.");

            string[] symbols = [.. (ToolArgs.Text(arguments, "symbols") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)];

            var started = _runner.TryStart(years, symbols);
            var status = await _runner.GetStatusAsync();

            _logger.LogInformation(
                "[CandleArchive] Backfill {Outcome} via agent tool (years={Years}, symbols={Symbols}).",
                started ? "started" : "already running",
                years?.ToString() ?? "configured",
                symbols.Length > 0 ? string.Join(",", symbols) : "all archived");

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                started,
                note = started
                    ? "The backfill is running in the background — it does NOT block this turn. Report " +
                      "that it started and how many trading days it is fetching; call status again later " +
                      "for progress. Roughly two seconds per trading day." +
                      (symbols.Length > 0
                          ? " The scope is checked once the pass begins, so a symbol that is not in the " +
                            "archive universe is refused there rather than here — if status shows no " +
                            "pass running, read progress.message for the reason."
                          : "")
                    : "A backfill pass is already running; the progress below is that pass. A second " +
                      "pass is deliberately not started.",
                status
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CandleArchive] {Action} failed.", action);
            return ToolResult.Fail($"Candle archive {action} failed: {ex.Message}");
        }
    }
}
