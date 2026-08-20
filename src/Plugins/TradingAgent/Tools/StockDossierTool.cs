using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.AhlAnalytics;
using TradingAgent.Config;

namespace TradingAgent.Tools;

/// <summary>
/// Assembles a dossier on one PSX symbol from the AHL research portal, one dimension at a time.
///
/// <para>
/// <b>Why dimensions rather than one fixed payload.</b> The portal can answer far more about a symbol
/// than any single response should carry — five years of candles, nineteen periods of statements
/// across forty-one ratios, hundreds of payout rows, full-text analyst notes. Returning all of it
/// would bury the answer and burn the context an agent needs to reason. So the caller names the
/// dimensions it wants, each maps to the smallest set of upstream calls that satisfies it, and
/// anything not asked for costs nothing.
/// </para>
///
/// <para>
/// This is the read surface a future autopilot is expected to sit on, so each dimension is
/// self-describing: units are stated, sector medians travel next to the value they contextualise, and
/// the two documented data hazards — adjusted candle prices and unconsolidated-only statements — are
/// reported inline rather than left for the consumer to rediscover.
/// </para>
/// </summary>
public sealed class StockDossierTool : BaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Dimensions the caller can request, and what each costs upstream.</summary>
    private static readonly string[] AllDimensions =
    [
        "quote",          // 0 extra calls — served from the shared snapshot
        "technicals",     // 0 extra calls — snapshot RSI/pivots/beta
        "levels",         // 0 extra calls — 52w range, circuit caps, historic prices
        "fundamentals",   // 1 call  — 41 ratios + sector median
        "valuation",      // 1 call  — the valuation subset of the above, for a compact answer
        "income",         // 1 call  — quarterly income statement
        "balance",        // 1 call  — annual balance sheet
        "profile",        // 1 call  — sector, fiscal year end, description
        "events",         // 2 calls — payouts/ex-dates + upcoming board meetings
        "payouts",        // 1 call  — full dividend/bonus/rights history
        "insiders",       // 1 call  — insider dealing for this symbol
        "news",           // 1 call  — recent headlines
        "research"        // 1 call  — AHL's own analyst notes, full text
    ];

    private readonly AhlAnalyticsClient _client;
    private readonly IRuntimePluginOptions<AhlAnalyticsConfig> _config;
    private readonly ILogger<StockDossierTool> _logger;

    public StockDossierTool(
        AhlAnalyticsClient client,
        IRuntimePluginOptions<AhlAnalyticsConfig> config,
        ILogger<StockDossierTool> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public override string Name => "stock_dossier";

    public override string Description =>
        "Deep research on ONE PSX symbol from the AHL analytics portal, by dimension. Dimensions: " +
        "quote (price, L1 bid/ask, volume, circuit caps), technicals (RSI, pivot support/resistance, " +
        "beta), levels (52-week range, position in range, returns over 1w–5y), fundamentals (41 ratios " +
        "— P/E, P/B, ROE, margins, leverage, working-capital days — each WITH its sector median so you " +
        "can tell cheap from cheap-for-a-reason), valuation (the compact subset), income and balance " +
        "(statements by period), profile (sector, fiscal year end, business description), events " +
        "(upcoming ex-dividend dates, book closures and board meetings — check this BEFORE holding " +
        "overnight), payouts (full dividend/bonus/rights history), insiders (director and executive " +
        "dealing), news, research (AHL's own analyst notes in full text). " +
        "Pass dimensions as a comma-separated list; defaults to quote,technicals,levels,valuation,events. " +
        "Only what you ask for is fetched.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["symbol"] = new()
        {
            Type = "string",
            Description = "PSX ticker symbol, e.g. LUCK, OGDC, MEBL.",
            Required = true
        },
        ["dimensions"] = new()
        {
            Type = "string",
            Description = "Comma-separated dimensions: " + string.Join(", ", AllDimensions) +
                          ". Use 'all' for everything (expensive — a dozen upstream calls). " +
                          "Defaults to quote,technicals,levels,valuation,events.",
            Required = false
        },
        ["statement_periods"] = new()
        {
            Type = "integer",
            Description = "How many periods of income/balance/fundamentals history to return (1-19). " +
                          "Defaults to 5. Statements go back 18 fiscal years plus TTM.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        if (!_client.Enabled)
        {
            return ToolResult.Fail(
                "The AHL analytics portal is disabled. Set Plugins:AhlAnalytics:Enabled to true.");
        }

        var symbol = ToolArgs.Text(arguments, "symbol")?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol))
            return ToolResult.Fail("symbol is required.");

        var requested = ParseDimensions(ToolArgs.Text(arguments, "dimensions"));
        var unknown = requested.Where(d => !AllDimensions.Contains(d)).ToList();
        if (unknown.Count > 0)
        {
            return ToolResult.Fail(
                $"Unknown dimension(s): {string.Join(", ", unknown)}. " +
                $"Valid: {string.Join(", ", AllDimensions)}.");
        }

        var periods = Math.Clamp(ToolArgs.Int(arguments, "statement_periods") ?? 5, 1, 19);

        var snapshot = await _client.GetMarketSnapshotAsync();
        if (snapshot?.Equities is null)
        {
            return ToolResult.Fail(
                "Could not read the market snapshot from the analytics portal. " +
                (_client.LastError ?? "No further detail was reported."));
        }

        if (!snapshot.Equities.TryGetValue(symbol, out var eq))
        {
            return ToolResult.Fail(
                $"'{symbol}' is not in the analytics portal's equity universe " +
                $"({snapshot.Equities.Count} symbols). Check the ticker.");
        }

        var dossier = new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["name"] = eq.Name,
            ["sector"] = AhlSectors.Name(eq.SectorCode) ?? eq.SectorCode,
            ["sector_code"] = eq.SectorCode,
            ["market_state"] = snapshot.MarketState,
            ["as_of"] = snapshot.LastUpdate,
            ["last_tick_at"] = eq.LastTickAt,
            // A symbol whose last tick predates the market's last update did not trade this session.
            // Saying so explicitly stops a stale price being read as a live one.
            ["traded_this_session"] =
                DatePart(eq.LastTickAt) is { } d && d == DatePart(snapshot.LastUpdate),
            ["dimensions_returned"] = requested
        };

        if (requested.Contains("quote")) dossier["quote"] = BuildQuote(eq);
        if (requested.Contains("technicals")) dossier["technicals"] = BuildTechnicals(eq);
        if (requested.Contains("levels")) dossier["levels"] = BuildLevels(eq);

        // Everything below costs upstream calls, so each is gated on being asked for.
        if (requested.Contains("fundamentals") || requested.Contains("valuation"))
        {
            var statement = await _client.GetStatementAsync(symbol, "fundamentals");
            var compact = requested.Contains("valuation") && !requested.Contains("fundamentals");
            dossier[compact ? "valuation" : "fundamentals"] =
                BuildRatios(statement, periods, compact);

            if (statement?.Data is not null)
                dossier["consolidation_note"] = ConsolidationNote(symbol);
        }

        if (requested.Contains("income"))
            dossier["income"] = BuildStatement(
                await _client.GetStatementAsync(symbol, "income", "quarterly"), periods);

        if (requested.Contains("balance"))
            dossier["balance"] = BuildStatement(
                await _client.GetStatementAsync(symbol, "balance", "annual"), periods);

        if (requested.Contains("profile"))
        {
            var profile = (await _client.GetProfileAsync(symbol))?.Data;
            dossier["profile"] = profile is null ? null : new
            {
                profile.Name,
                sector = profile.SectorName,
                description = Truncate(profile.Description, 1200),
                profile.Website,
                profile.Employees,
                // Stated because a quarterly result cannot be read without it: "Q2" spans different
                // calendar months for a June-end company than a December-end one.
                fiscal_year_end = profile.YearEnd,
                par_value = profile.ParValue,
                profile.Auditors
            };
        }

        if (requested.Contains("events"))
            dossier["events"] = await BuildEventsAsync(symbol, eq);

        if (requested.Contains("payouts"))
        {
            var payouts = await _client.GetPayoutHistoryAsync(symbol);
            dossier["payouts"] = payouts
                .Where(p => !string.IsNullOrWhiteSpace(p.ExDate) || IsPositive(p.Dividend) ||
                            IsPositive(p.Bonus) || IsPositive(p.RightPrice))
                .Take(25)
                .Select(p => new
                {
                    p.Date,
                    ex_date = p.ExDate,
                    dividend = p.Dividend,
                    bonus = p.Bonus,
                    right_price = p.RightPrice,
                    book_closure_from = p.BookClosureFrom,
                    book_closure_to = p.BookClosureTo,
                    period_end = p.PeriodEnd,
                    p.Quarter,
                    eps = p.QuarterEps
                });
        }

        if (requested.Contains("insiders"))
        {
            var insiders = await _client.GetInsiderTransactionsAsync(symbol);
            var buys = insiders.Count(i => string.Equals(i.Type, "buy", StringComparison.OrdinalIgnoreCase));
            var sells = insiders.Count(i => string.Equals(i.Type, "sell", StringComparison.OrdinalIgnoreCase));
            dossier["insiders"] = new
            {
                total = insiders.Count,
                buys,
                sells,
                net_shares = insiders.Sum(i =>
                    string.Equals(i.Type, "buy", StringComparison.OrdinalIgnoreCase)
                        ? i.Shares ?? 0 : -(i.Shares ?? 0)),
                recent = insiders.Take(15).Select(i => new
                {
                    // notice_date first: it is when the market could have known, which is what a
                    // signal must key off. dealt_date can precede it by days.
                    notice_date = i.NoticeDate,
                    dealt_date = i.DealtDate,
                    i.Type,
                    person = i.PersonName,
                    role = i.Role,
                    i.Shares,
                    i.Price
                })
            };
        }

        if (requested.Contains("news"))
            dossier["news"] = (await _client.GetNewsAsync(symbol)).Take(10).Select(n => new
            {
                n.Date,
                n.Title,
                summary = Truncate(n.Description, 400),
                n.Source,
                n.Link
            });

        if (requested.Contains("research"))
            dossier["research"] = (await _client.GetResearchNotesAsync(symbol, 5)).Select(r => new
            {
                title = r.Title,
                // AHL's notes carry the substance in the body, so it is kept long — this is the
                // broker's own view and the reason the dimension exists.
                body = Truncate(r.Body, 2500)
            });

        _logger.LogDebug("[StockDossier] {Symbol}: {Dimensions}.", symbol, string.Join(",", requested));
        return ToolResult.Ok(JsonSerializer.Serialize(dossier, JsonOptions));
    }

    // ── snapshot-backed dimensions (no upstream cost) ─────────────────────────

    private static object BuildQuote(AhlEquity eq) => new
    {
        price = eq.Close,
        open = eq.Open,
        high = eq.High,
        low = eq.Low,
        previous_close = eq.PreviousClose,
        change = eq.Change,
        change_percent = eq.ChangeFraction * 100m,
        volume = eq.Volume,
        trades = eq.TradeCount,
        average_price = eq.AveragePrice,
        turnover_pkr = eq.Close * eq.Volume,
        // L1 only — this portal has no order-book ladder, and all four are 0 outside market hours.
        bid = eq.BidPrice,
        bid_volume = eq.BidVolume,
        ask = eq.AskPrice,
        ask_volume = eq.AskVolume,
        depth_note = "Best bid/ask only. This portal carries no L2 order book; use the broker feed " +
                     "for market depth. All four are 0 when the market is closed.",
        upper_cap = eq.UpperCap,
        lower_lock = eq.LowerLock,
        band_percent = eq.BandPercent,
        volume_vs_avg_10d = eq.AvgVolume10Day is > 0 ? eq.Volume / eq.AvgVolume10Day : null,
        free_float = eq.FreeFloat,
        shares_outstanding = eq.SharesOutstanding,
        market_cap_pkr = eq.Close * eq.SharesOutstanding,
        ex_dividend = eq.ExDividend,
        ex_bonus = eq.ExBonus,
        ex_rights = eq.ExRights,
        indices = eq.ListedIn
    };

    private static object BuildTechnicals(AhlEquity eq) => new
    {
        rsi = eq.Rsi,
        rsi_reading = eq.Rsi switch
        {
            null => null,
            > 70 => "overbought",
            < 30 => "oversold",
            _ => "neutral"
        },
        std_dev = eq.StdDev,
        pivot_points = eq.PivotPoints is null ? null : new
        {
            pivot = eq.PivotPoints.Pivot,
            resistance = new[] { eq.PivotPoints.R1, eq.PivotPoints.R2, eq.PivotPoints.R3 },
            support = new[] { eq.PivotPoints.S1, eq.PivotPoints.S2, eq.PivotPoints.S3 }
        },
        beta = eq.Beta is null ? null : new
        {
            one_month = eq.Beta.OneMonth,
            three_month = eq.Beta.ThreeMonth,
            six_month = eq.Beta.SixMonth,
            one_year = eq.Beta.OneYear
        },
        source_note = "Portal-computed on daily bars. The portal's own precomputed /indicators " +
                      "endpoint disagrees with values computed from its candles, so prefer " +
                      "analyze_candles for indicator-driven decisions."
    };

    private static object BuildLevels(AhlEquity eq)
    {
        // Where price sits between the 52-week extremes, 0 = at the low, 100 = at the high. More
        // useful than the raw pair because it answers "is this extended" in one number.
        decimal? rangePosition = eq.High52Week is > 0 && eq.Low52Week is not null &&
                                 eq.High52Week > eq.Low52Week && eq.Close is not null
            ? (eq.Close.Value - eq.Low52Week.Value) /
              (eq.High52Week.Value - eq.Low52Week.Value) * 100m
            : null;

        return new
        {
            high_52w = eq.High52Week,
            low_52w = eq.Low52Week,
            range_position_percent = rangePosition is null
                ? (decimal?)null
                : Math.Round(rangePosition.Value, 1),
            upper_cap = eq.UpperCap,
            lower_lock = eq.LowerLock,
            returns_percent = new
            {
                one_week = Return(eq.Close, eq.Price1WeekAgo),
                one_month = Return(eq.Close, eq.Price1MonthAgo),
                three_month = Return(eq.Close, eq.Price3MonthsAgo),
                six_month = Return(eq.Close, eq.Price6MonthsAgo),
                one_year = Return(eq.Close, eq.Price1YearAgo),
                ytd = Return(eq.Close, eq.PriceYearStart),
                five_year = Return(eq.Close, eq.Price5YearsAgo)
            }
        };
    }

    private static decimal? Return(decimal? now, decimal? then) =>
        now is not null && then is > 0
            ? Math.Round((now.Value - then.Value) / then.Value * 100m, 2)
            : null;

    // ── statement dimensions ──────────────────────────────────────────────────

    /// <summary>
    /// Ratios with their sector median attached. The median is the point of this dimension: a P/E of 8
    /// means nothing until you know the sector trades at 14 — or at 6.
    /// </summary>
    private static object? BuildRatios(AhlStatementResponse? response, int periods, bool compact)
    {
        var data = response?.Data;
        if (data?.Fields is null || data.Periods is null) return null;

        // The compact "valuation" view: the ratios a trading decision actually turns on.
        var wanted = compact
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
              { "pe_ratio", "pb_ratio", "ps_ratio", "eps", "dps", "div_yield", "payout",
                "roe", "roa", "npm", "bkv", "ltde", "cur_ratio" }
            : null;

        var stats = data.SectorStats?
            .Where(s => s.Key is not null)
            .ToDictionary(s => s.Key!, s => s, StringComparer.OrdinalIgnoreCase);

        var periodLabels = data.Periods
            .Take(periods)
            .Select(p => p.Quarter is { Length: > 0 }
                ? $"{p.Quarter} {p.Year}"
                : p.Year ?? p.PeriodEnd ?? "?")
            .ToList();

        var rows = data.Fields
            // key is null on some statement rows, so fall back to the label rather than dropping them.
            .Where(f => wanted is null || (f.Key is not null && wanted.Contains(f.Key)))
            .Select(f =>
            {
                var stat = f.Key is not null && stats is not null && stats.TryGetValue(f.Key, out var s)
                    ? s : null;
                var values = f.Values?.Take(periods).ToList();
                var latest = values?.FirstOrDefault();

                return new
                {
                    key = f.Key ?? f.Label,
                    label = f.Label,
                    unit = f.Unit,
                    latest,
                    values,
                    sector_median = stat?.Median,
                    sector_min = stat?.Min,
                    sector_max = stat?.Max,
                    // A single word beats making the consumer compare two numbers it might invert.
                    vs_sector = latest is not null && stat?.Median is not null
                        ? latest > stat.Median ? "above" : latest < stat.Median ? "below" : "at"
                        : null
                };
            })
            .ToList();

        return new { periods = periodLabels, ratios = rows };
    }

    private static object? BuildStatement(AhlStatementResponse? response, int periods)
    {
        var data = response?.Data;
        if (data?.Fields is null || data.Periods is null) return null;

        return new
        {
            interval = data.Interval,
            // Always false on this API — the consolidated parameter is ignored. Reported so a
            // consumer never assumes it got group numbers.
            consolidated = data.Consolidated,
            periods = data.Periods.Take(periods).Select(p => p.Quarter is { Length: > 0 }
                ? $"{p.Quarter} {p.Year}"
                : p.Year ?? p.PeriodEnd ?? "?"),
            lines = data.Fields.Select(f => new
            {
                key = f.Key ?? f.Label,
                label = f.Label,
                values = f.Values?.Take(periods)
            })
        };
    }

    private string ConsolidationNote(string symbol)
    {
        var flagged = _config.Current.ConsolidationWarningSymbols
            .Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase));

        var note = "The portal serves UNCONSOLIDATED statements only — the consolidated query " +
                   "parameter is accepted and ignored.";
        return flagged
            ? note + $" {symbol} is a group with material subsidiaries, so these figures understate " +
                     "group earnings substantially. Do not present them as the company's total."
            : note;
    }

    // ── events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forward-looking event risk: ex-dates and book closures still ahead, plus any scheduled board
    /// meeting. This is the dimension a position-holding decision should consult — PSX day orders are
    /// cancelled at the close and protective stops do not survive overnight, so an unplanned hold
    /// through an event is unhedged exposure.
    /// </summary>
    private async Task<object> BuildEventsAsync(string symbol, AhlEquity eq)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5)); // PKT

        var payouts = await _client.GetPayoutHistoryAsync(symbol);
        var upcoming = payouts
            .Select(p => new
            {
                p.Title,
                ex_date = p.ExDate,
                dividend = p.Dividend,
                bonus = p.Bonus,
                right_price = p.RightPrice,
                book_closure_from = p.BookClosureFrom,
                book_closure_to = p.BookClosureTo,
                parsed = ParseDate(p.ExDate)
            })
            .Where(p => p.parsed is not null && p.parsed >= today)
            .OrderBy(p => p.parsed)
            .Take(5)
            .ToList();

        var meetings = (await _client.GetBoardMeetingsAsync())
            .Where(m => string.Equals(m.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .Select(m =>
            {
                var details = AhlAnnouncementDetails.Parse(m.Details);
                return new
                {
                    m.Title,
                    scheduled = details.GetValueOrDefault("datetime") ?? m.HeldDate,
                    location = details.GetValueOrDefault("location") ?? m.Location,
                    agenda = details.GetValueOrDefault("agenda") ?? m.Agenda,
                    period_end = details.GetValueOrDefault("periodEndDate") ?? m.PeriodEnd
                };
            })
            .Take(5)
            .ToList();

        var flags = new List<string>();
        if (eq.ExDividend == true) flags.Add("trading ex-dividend");
        if (eq.ExBonus == true) flags.Add("trading ex-bonus");
        if (eq.ExRights == true) flags.Add("trading ex-rights");
        if (upcoming.Count > 0) flags.Add($"ex-date {upcoming[0].ex_date} ahead");
        if (meetings.Count > 0) flags.Add($"board meeting {meetings[0].scheduled}");

        return new
        {
            // The one-line answer to "is it safe to hold this", ahead of the detail.
            has_event_risk = flags.Count > 0,
            flags,
            currently_ex = new
            {
                dividend = eq.ExDividend ?? false,
                bonus = eq.ExBonus ?? false,
                rights = eq.ExRights ?? false
            },
            upcoming_payouts = upcoming.Select(p => new
            {
                p.Title, p.ex_date, p.dividend, p.bonus, p.right_price,
                p.book_closure_from, p.book_closure_to
            }),
            board_meetings = meetings,
            note = "PSX day orders are cancelled at market close and protective stops do not survive " +
                   "overnight, so holding through an event carries unhedged exposure."
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static List<string> ParseDimensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ["quote", "technicals", "levels", "valuation", "events"];

        if (raw.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return [.. AllDimensions];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(d => d.ToLowerInvariant())
                  .Distinct()
                  .ToList();
    }

    private static string? DatePart(string? timestamp) =>
        timestamp is { Length: >= 10 } ? timestamp[..10] : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;

    private static bool IsPositive(string? numeric) =>
        decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d > 0;

    private static string? Truncate(string? text, int max) =>
        text is null ? null : text.Length <= max ? text : text[..max] + "…";
}
