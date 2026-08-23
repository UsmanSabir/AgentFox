using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Research;
using TradingAgent.Watchlist;

namespace TradingAgent.Tools;

/// <summary>
/// Scans the configured symbol list for candle-based setups: which stocks are sitting at support
/// (the buy-low case) and which are pressing resistance (the sell-high case).
///
/// The universe defaults to the user's monitoring list — the editable watchlist plus
/// <see cref="TradingAgentOptions.AllowedSymbols"/> (see
/// <see cref="TradingAgent.Watchlist.MonitoredUniverse"/>). Scanning a superset of the tradable list
/// is deliberate: a watched symbol should show up in a scan. Each result therefore carries
/// <c>tradable</c>, because a candidate outside the selected execution universe is information only —
/// the risk engine will refuse an order for it, and the caller must say so rather than recommend it.
///
/// Ranking is deterministic (<see cref="TechnicalAnalyzer"/>), and stocks at the bottom of their
/// range because they are still falling are reported under <c>avoid</c>, never as buy candidates.
/// Nothing here places or approves an order.
/// </summary>
public sealed class ScanWatchlistTool : BaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Upper bound on symbols scanned in one call, so a huge list cannot stall a turn.</summary>
    private const int MaxUniverse = 200;

    /// <summary>Above this many symbols the per-symbol watchlist table is omitted to keep the result readable.</summary>
    private const int WatchlistTableLimit = 60;

    private readonly PsxDataClient _dataClient;
    private readonly CandleHistoryProvider _history;
    private readonly MonitoredUniverse _universe;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<ScanWatchlistTool> _logger;

    public ScanWatchlistTool(
        PsxDataClient dataClient,
        CandleHistoryProvider history,
        MonitoredUniverse universe,
        IOptions<TradingAgentOptions> options,
        ILogger<ScanWatchlistTool> logger)
    {
        _dataClient = dataClient;
        _history = history;
        _universe = universe;
        _options = options;
        _logger = logger;
    }

    public override string Name => "scan_watchlist";

    public override string Description =>
        "Scan the configured allowed-symbols watchlist using daily AND weekly candles, returning ranked " +
        "BUY candidates trading at support and SELL candidates pressing resistance, each with the level, " +
        "distance to it, RSI/ATR/volume context, and a suggested entry, stop, target and reward:risk. " +
        "Candidates whose entry level is confirmed on the weekly chart rank first, and anything breaking " +
        "down on either timeframe is returned under 'avoid' rather than as a buy — including a tidy " +
        "daily support test inside a weekly breakdown. Call this FIRST for any 'recommend a stock', " +
        "'what should I buy today', or daily-scan request; pass holdings from get_portfolio to rank " +
        "sell candidates you actually own.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["side"] = new()
        {
            Type = "string",
            Description = "Which setups to return: 'buy' (at support), 'sell' (at resistance), or 'both'. Default 'buy'.",
            EnumValues = ["buy", "sell", "both"],
            Required = false
        },
        ["symbols"] = new()
        {
            Type = "array",
            Description = "Optional explicit symbols to scan instead of the configured allowed-symbols list. " +
                          "Accepts an array or a comma-separated string. Note that symbols outside the " +
                          "allowed list cannot be executed by the risk engine.",
            Required = false,
            JsonSchema = """{ "type": "array", "items": { "type": "string" } }"""
        },
        ["lookback_days"] = new()
        {
            Type = "integer",
            Description = "Trading sessions of candle history per symbol (5-250). Defaults to the configured scan lookback.",
            Required = false
        },
        ["min_reward_risk"] = new()
        {
            Type = "number",
            Description = "Minimum (target-entry)/(entry-stop) for a buy candidate. Defaults to the configured floor. Pass 0 to disable.",
            Required = false
        },
        ["max_results"] = new()
        {
            Type = "integer",
            Description = "Maximum candidates per side. Defaults to the configured limit.",
            Required = false
        },
        ["min_average_volume"] = new()
        {
            Type = "integer",
            Description = "Minimum 30-session average volume for a candidate. Defaults to the configured floor.",
            Required = false
        },
        ["holdings"] = new()
        {
            Type = "array",
            Description = "Optional current holdings from get_portfolio, used to mark owned stocks and " +
                          "compute unrealized P&L on sell candidates.",
            Required = false,
            JsonSchema = """
                {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "symbol":            { "type": "string" },
                      "quantity":          { "type": "number" },
                      "average_buy_price": { "type": "number" }
                    },
                    "required": ["symbol"]
                  }
                }
                """
        }
    };

    /// <summary>One holding as supplied by the caller (normally copied straight from get_portfolio).</summary>
    private sealed record HoldingInput(string? Symbol, decimal? Quantity, decimal? AverageBuyPrice);

    /// <summary>One analysed symbol: the daily read, the weekly corroboration, and any position held.</summary>
    private sealed record Candidate(
        TechnicalSnapshot Snapshot,
        MultiTimeframeView Multi,
        HoldingInput? Holding);

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var options = _options.Value;
        var scan = options.Scan;

        var side = (ToolArgs.Text(arguments, "side") ?? "buy").Trim().ToLowerInvariant();
        if (side is not ("buy" or "sell" or "both"))
            return ToolResult.Fail("Parameter 'side' must be 'buy', 'sell', or 'both'.");

        var lookback = ToolArgs.Int(arguments, "lookback_days") ?? scan.LookbackDays;
        var minRewardRisk = ToolArgs.Decimal(arguments, "min_reward_risk") ?? scan.MinRewardRisk;
        var maxResults = Math.Clamp(ToolArgs.Int(arguments, "max_results") ?? scan.MaxResults, 1, 50);
        var minVolume = ToolArgs.Long(arguments, "min_average_volume") ?? scan.MinAverageVolume;

        var notes = new List<string>();
        var excluded = new List<object>();

        // ── Universe ──────────────────────────────────────────────────────────
        var explicitSymbols = ParseSymbols(arguments.GetValueOrDefault("symbols"));
        var monitored = await _universe.ForMonitoringAsync();
        var universeSource = explicitSymbols.Count > 0 ? "explicit_symbols" : "watchlist";
        var requested = explicitSymbols.Count > 0 ? explicitSymbols : (IReadOnlyList<string>)monitored;

        var universe = new List<string>();
        foreach (var candidate in requested)
        {
            try
            {
                var normalized = PsxDataClient.NormalizeStockSymbol(candidate ?? "");
                if (!universe.Contains(normalized)) universe.Add(normalized);
            }
            catch (ArgumentException)
            {
                excluded.Add(new { symbol = candidate, reason = "Not a valid PSX ticker." });
            }
        }

        if (universe.Count == 0)
            return ToolResult.Fail(
                "There are no symbols to scan. Add PSX tickers to the watchlist on the Trading page, " +
                "configure the selected execution source, or pass 'symbols' explicitly.");

        if (universe.Count > MaxUniverse)
        {
            notes.Add($"Universe truncated from {universe.Count} to {MaxUniverse} symbols for this scan.");
            universe = universe.Take(MaxUniverse).ToList();
        }

        var holdings = ParseHoldings(arguments.GetValueOrDefault("holdings"), notes);

        // Resolved once for the whole scan: which of these symbols an order could actually be placed
        // for. Everything else here is analysis, which is allowed to range wider.
        var tradable = (await _universe.ForExecutionAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var monitorOnly = universe.Count(sym => !tradable.Contains(sym));
        if (monitorOnly > 0)
            notes.Add($"{monitorOnly} of {universe.Count} scanned symbols are monitor-only: they are on "
                    + "the watchlist but not in the selected execution universe, so an order for them would be rejected. "
                    + "Each result carries 'tradable'.");

        // Scanned and reported like any other symbol — a hand-managed name is exactly one you want a
        // setup called out on. What changes is who acts: the note says hand it over rather than trade it.
        var manualOnly = await _universe.ManualOnlyAsync();
        var manualScanned = universe.Count(sym => manualOnly.Contains(sym));
        if (manualScanned > 0)
            notes.Add($"{manualScanned} of {universe.Count} scanned symbols are manual-only: report any "
                    + "setup on them to the operator to place, and do not place it yourself. Each result "
                    + "carries 'manual_only'.");

        // ── Candles ───────────────────────────────────────────────────────────
        // Weekly structure needs a far deeper window than the daily read; the archive serves it
        // locally, so asking for it costs a query rather than hundreds of portal requests.
        var weeklySessions = Math.Clamp(scan.WeeklyLookbackWeeks, 12, 600) * 6;
        var sessionsWanted = Math.Max(lookback, weeklySessions);

        CandleHistory history;
        try
        {
            _logger.LogInformation(
                "[ScanWatchlist] Scanning {Count} symbols over {Days} sessions (side={Side}).",
                universe.Count, lookback, side);
            history = await _history.GetDailyAsync(universe, sessionsWanted, includeLive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ScanWatchlist] Candle load failed.");
            return ToolResult.Fail($"Candle data could not be loaded: {ex.Message}");
        }

        if (history.Sessions.Count == 0)
            return ToolResult.Fail(
                "No settled trading sessions are available from the archive or the PSX portal, so no " +
                "support or resistance levels can be computed. Check connectivity to the data portal.");

        // ── Analyze ───────────────────────────────────────────────────────────
        var technicalOptions = TechnicalOptions.From(scan);
        var analyzed = new List<Candidate>();

        foreach (var symbol in universe)
        {
            if (!history.Series.TryGetValue(symbol, out var candles) || candles.Count == 0)
            {
                excluded.Add(new { symbol, reason = "No candles in the PSX market summary for this window." });
                continue;
            }

            // The daily read stays scoped to the requested lookback; the weekly rollup uses everything
            // archived, so a deeper archive strengthens the weekly view without moving daily levels.
            var dailyWindow = candles.TakeLast(lookback).ToList();
            var snapshot = TechnicalAnalyzer.Analyze(symbol, dailyWindow, technicalOptions);

            if (snapshot.Setup == TradeSetup.InsufficientData)
            {
                excluded.Add(new { symbol, reason = $"Only {snapshot.Bars} sessions of history available." });
                continue;
            }

            if (minVolume > 0 && snapshot.AverageVolume is { } avg && avg < minVolume)
            {
                excluded.Add(new
                {
                    symbol,
                    reason = $"30-day average volume {avg:N0} is below the {minVolume:N0} floor — " +
                             "levels here may not be tradable at the quoted price."
                });
                continue;
            }

            var multi = MultiTimeframeAnalyzer.Analyze(
                symbol, candles, technicalOptions, scan.ConfluenceTolerancePercent);

            analyzed.Add(new Candidate(snapshot, multi, holdings.GetValueOrDefault(symbol)));
        }

        // ── Rank ──────────────────────────────────────────────────────────────
        var buyCandidates = new List<object>();
        var buyRejected = new List<object>();
        if (side is "buy" or "both")
        {
            var eligible = analyzed
                .Where(a => a.Snapshot.Setup == TradeSetup.BuyAtSupport)
                // A daily dip inside a WEEKLY breakdown is the falling-knife case one timeframe up:
                // the daily levels look tidy precisely because the weekly one is collapsing through
                // them. These are reported under `avoid`, never offered as buys.
                .Where(a => !a.Multi.WeeklyBreakdown)
                .ToList();

            foreach (var item in eligible)
            {
                if (minRewardRisk > 0
                    && (item.Snapshot.RewardRiskRatio is null || item.Snapshot.RewardRiskRatio < minRewardRisk))
                {
                    buyRejected.Add(new
                    {
                        symbol = item.Snapshot.Symbol,
                        reward_risk = item.Snapshot.RewardRiskRatio,
                        reason = item.Snapshot.RewardRiskRatio is null
                            ? "Reward:risk could not be computed (no resistance above, or no ATR for a stop)."
                            : $"Reward:risk below the {minRewardRisk} floor."
                    });
                }
            }

            buyCandidates = eligible
                .Where(a => minRewardRisk <= 0
                    || (a.Snapshot.RewardRiskRatio is { } rr && rr >= minRewardRisk))
                // Weekly-confirmed entry levels first, then aligned timeframes, then proximity and
                // reward:risk. A level two timeframes recognise outranks one that is merely closer.
                .OrderByDescending(a => a.Multi.EntryLevelConfirmedWeekly)
                .ThenByDescending(a => a.Multi.Alignment == TimeframeAlignment.Aligned)
                .ThenBy(a => a.Snapshot.PercentAboveSupport ?? decimal.MaxValue)
                .ThenByDescending(a => a.Snapshot.RewardRiskRatio ?? 0m)
                .Take(maxResults)
                .Select(a => Project(a, tradable, manualOnly))
                .ToList();
        }

        var sellCandidates = new List<object>();
        if (side is "sell" or "both")
        {
            sellCandidates = analyzed
                .Where(a => a.Snapshot.Setup == TradeSetup.SellAtResistance)
                // Owned positions first: those are the sells that can actually be acted on. Then
                // weekly-confirmed resistance and timeframe agreement, which make rejection likelier,
                // and only then raw proximity — same precedence as the buy side.
                .OrderByDescending(a => a.Holding is not null)
                .ThenByDescending(a => a.Multi.ConfirmedResistances.Count > 0)
                .ThenByDescending(a => a.Multi.Alignment == TimeframeAlignment.Aligned)
                .ThenBy(a => a.Snapshot.PercentBelowResistance ?? decimal.MaxValue)
                .Take(maxResults)
                .Select(a => Project(a, tradable, manualOnly))
                .ToList();
        }

        var avoid = analyzed
            .Where(a => a.Snapshot.Setup == TradeSetup.AvoidBreakdown || a.Multi.WeeklyBreakdown)
            .OrderBy(a => a.Snapshot.Symbol)
            .Select(a => new
            {
                symbol = a.Snapshot.Symbol,
                close = a.Snapshot.Close,
                range_low = a.Snapshot.RangeLow,
                consecutive_down_days = a.Snapshot.ConsecutiveDownDays,
                held = a.Holding is not null,
                daily_setup = a.Snapshot.Setup,
                weekly_setup = a.Multi.Weekly?.Setup,
                reason = a.Multi.WeeklyBreakdown && a.Snapshot.Setup != TradeSetup.AvoidBreakdown
                    ? "The WEEKLY chart is breaking down. The daily levels may look like a clean support " +
                      "test, but price is falling through structure one timeframe up."
                    : "At the bottom of its range while still falling — a breakdown, not a support test."
            })
            .ToList();

        var scope = ResearchReferenceScope.Current;
        if (scope is not null)
        {
            foreach (var url in _dataClient.CandleSourceUrls())
                scope.Add(url, "PSX market candles", "PSX Data Portal");
        }

        if (analyzed.Count > WatchlistTableLimit)
            notes.Add($"Per-symbol watchlist table omitted for {analyzed.Count} symbols; " +
                      "call analyze_candles for any individual symbol.");

        var latest = history.Sessions.Count > 0 ? history.Sessions[^1] : (DateOnly?)null;

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            universe_source = universeSource,
            side,
            scanned = analyzed.Count,
            requested_symbols = universe.Count,
            market = new
            {
                sessions_loaded = history.Sessions.Count,
                latest_settled_session = latest?.ToString("yyyy-MM-dd"),
                live_bar_used = analyzed.Any(a => a.Snapshot.UsesLiveBar)
            },
            thresholds = new
            {
                lookback_days = lookback,
                support_proximity_percent = scan.SupportProximityPercent,
                resistance_proximity_percent = scan.ResistanceProximityPercent,
                min_reward_risk = minRewardRisk,
                min_average_volume = minVolume
            },
            buy_candidates = buyCandidates,
            sell_candidates = sellCandidates,
            avoid,
            buy_rejected_on_reward_risk = buyRejected,
            watchlist = analyzed.Count <= WatchlistTableLimit
                ? analyzed
                    .OrderBy(a => a.Snapshot.Symbol)
                    .Select(a => new
                    {
                        symbol = a.Snapshot.Symbol,
                        close = a.Snapshot.Close,
                        day_change_percent = a.Snapshot.DayChangePercent,
                        zone = a.Snapshot.Zone,
                        setup = a.Snapshot.Setup,
                        weekly_setup = a.Multi.Weekly?.Setup,
                        timeframe_alignment = a.Multi.Alignment,
                        percent_above_support = a.Snapshot.PercentAboveSupport,
                        percent_below_resistance = a.Snapshot.PercentBelowResistance,
                        range_position = a.Snapshot.RangePosition,
                        rsi14 = a.Snapshot.Rsi14,
                        held = a.Holding is not null
                    })
                    .ToList()
                : null,
            excluded,
            notes,
            warnings = history.Warnings,
            retrieved_at_utc = history.RetrievedAtUtc,
            source_urls = _dataClient.CandleSourceUrls()
        }, JsonOptions));
    }

    /// <summary>Flattens a candidate into the compact row the agent reasons over.</summary>
    private static object Project(
        Candidate candidate, IReadOnlySet<string> tradable, IReadOnlySet<string> manualOnly)
    {
        var s = candidate.Snapshot;
        var multi = candidate.Multi;
        var holding = candidate.Holding;

        decimal? unrealizedPercent = holding?.AverageBuyPrice is > 0
            ? Math.Round((s.Close - holding.AverageBuyPrice!.Value) / holding.AverageBuyPrice.Value * 100m, 2)
            : null;

        return new
        {
            symbol = s.Symbol,
            // Whether an ORDER for this symbol can pass the risk engine. The scan universe is the
            // monitoring universe, which may be wider than the selected execution source, so a candidate can be a
            // genuine setup and still not be executable — the caller must say so, not imply a trade.
            tradable = tradable.Contains(s.Symbol),
            // Tradable, but not BY YOU-the-agent: the operator hand-manages this name. Reported beside
            // 'tradable' because the two refusals have different remedies — one needs a config change,
            // this one needs a human to place the order.
            manual_only = manualOnly.Contains(s.Symbol),
            close = s.Close,
            as_of = s.AsOf.ToString("yyyy-MM-dd"),
            uses_live_bar = s.UsesLiveBar,
            day_change_percent = s.DayChangePercent,
            zone = s.Zone,
            setup = s.Setup,
            nearest_support = s.NearestSupport,
            percent_above_support = s.PercentAboveSupport,
            nearest_resistance = s.NearestResistance,
            percent_below_resistance = s.PercentBelowResistance,
            range_low = s.RangeLow,
            range_high = s.RangeHigh,
            range_position = s.RangePosition,
            suggested_entry = s.SuggestedEntry,
            suggested_stop = s.SuggestedStop,
            suggested_target = s.SuggestedTarget,
            reward_risk = s.RewardRiskRatio,
            trend = s.Trend,
            rsi14 = s.Rsi14,
            atr_percent = s.AtrPercent,
            volume_ratio = s.VolumeRatio,
            average_volume_30d = s.AverageVolume,
            consecutive_down_days = s.ConsecutiveDownDays,
            consecutive_up_days = s.ConsecutiveUpDays,
            supports = s.Supports,
            resistances = s.Resistances,

            // Higher-timeframe corroboration: whether the weekly chart recognises the same levels,
            // and whether the two timeframes point the same way.
            weekly_zone = multi.Weekly?.Zone,
            weekly_setup = multi.Weekly?.Setup,
            weekly_nearest_support = multi.Weekly?.NearestSupport,
            weekly_nearest_resistance = multi.Weekly?.NearestResistance,
            weekly_bars = multi.WeeklyBars,
            timeframe_alignment = multi.Alignment,
            entry_level_confirmed_weekly = multi.EntryLevelConfirmedWeekly,
            confirmed_supports = multi.ConfirmedSupports,
            confirmed_resistances = multi.ConfirmedResistances,
            multi_timeframe_notes = multi.Notes,

            holding = holding is null ? null : new
            {
                quantity = holding.Quantity,
                average_buy_price = holding.AverageBuyPrice,
                unrealized_percent = unrealizedPercent
            },
            reasons = s.Reasons
        };
    }

    /// <summary>Accepts a JSON array, a single string, or a comma/space separated list.</summary>
    private static List<string> ParseSymbols(object? value)
    {
        if (value is null) return [];

        try
        {
            var json = JsonSerializer.Serialize(value);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .ToList();
            }

            var text = document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : document.RootElement.ToString();

            return (text ?? "")
                .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, HoldingInput> ParseHoldings(object? value, List<string> notes)
    {
        var map = new Dictionary<string, HoldingInput>(StringComparer.OrdinalIgnoreCase);
        if (value is null) return map;

        try
        {
            var json = JsonSerializer.Serialize(value);
            var parsed = JsonSerializer.Deserialize<List<HoldingInput>>(json, JsonOptions) ?? [];
            foreach (var holding in parsed)
            {
                if (string.IsNullOrWhiteSpace(holding.Symbol)) continue;
                map[holding.Symbol.Trim().ToUpperInvariant()] = holding;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            notes.Add($"'holdings' could not be parsed and was ignored: {ex.Message}");
        }

        return map;
    }
}
