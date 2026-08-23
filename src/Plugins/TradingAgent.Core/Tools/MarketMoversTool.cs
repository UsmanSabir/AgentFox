using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.AhlAnalytics;

namespace TradingAgent.Tools;

/// <summary>
/// Runs a market-wide mover screen over the AHL analytics snapshot — gainers, losers, most active,
/// unusual volume, gaps, circuit-cap proximity — plus session breadth and sector rotation.
///
/// <para>
/// One snapshot fetch backs every screen, so asking for several costs no more upstream traffic than
/// asking for one. Every number is computed from the snapshot by <see cref="AhlMovers"/>; no model is
/// involved in producing them, so the agent can quote them directly.
/// </para>
///
/// <para>
/// Symbols that did not trade in the market's latest session are excluded before ranking. That is not
/// a nicety: the snapshot carries every listed instrument with its last-known change, so without the
/// filter a "top gainers" list fills with names whose last tick was months ago.
/// </para>
/// </summary>
public sealed class MarketMoversTool : BaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AhlAnalyticsClient _client;
    private readonly ILogger<MarketMoversTool> _logger;

    public MarketMoversTool(AhlAnalyticsClient client, ILogger<MarketMoversTool> logger)
    {
        _client = client;
        _logger = logger;
    }

    public override string Name => "market_movers";

    public override string Description =>
        "Scan the WHOLE PSX market (857 equities) for today's movers in one call. Screens: " +
        "gainers, losers, most_active (by share volume), most_valuable (by traded value — usually " +
        "more meaningful than volume on PSX, where penny stocks dominate share counts), " +
        "unusual_volume (volume vs its own 10-day average, the continuation screen), gap_up, " +
        "gap_down, near_upper_cap and near_lower_lock (symbols with no headroom left in the day's " +
        "circuit band — an order beyond the cap is refused, so this changes what you can place). " +
        "Also returns market breadth (advancing/declining, turnover split) and sector rotation. " +
        "Filter by index (e.g. KSE100), sector code, minimum turnover/volume/price to keep results " +
        "tradable. Only symbols that actually traded in the latest session are ranked. " +
        "Use this to find candidates; use stock_dossier or analyze_candles to study one.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["screen"] = new()
        {
            Type = "string",
            Description = "Which screen to run: " + string.Join(", ", AhlMovers.ScreenNames) +
                          ". Defaults to gainers.",
            Required = false
        },
        ["limit"] = new()
        {
            Type = "integer",
            Description = "Rows to return (1-100). Defaults to 10.",
            Required = false
        },
        ["index"] = new()
        {
            Type = "string",
            Description = "Restrict to members of an index, e.g. KSE100, KSE30, KMI30, ALLSHR. " +
                          "Strongly recommended for tradable results — the unrestricted market " +
                          "includes hundreds of illiquid names.",
            Required = false
        },
        ["sector_code"] = new()
        {
            Type = "string",
            Description = "Restrict to a PSX sector code, e.g. 0804 (Cement), 0807 (Commercial Banks), " +
                          "0820 (Oil & Gas Exploration).",
            Required = false
        },
        ["min_turnover_pkr"] = new()
        {
            Type = "number",
            Description = "Minimum traded value in PKR — the practical liquidity floor. " +
                          "e.g. 10000000 for names that turned over at least Rs 10mn today.",
            Required = false
        },
        ["min_volume"] = new()
        {
            Type = "integer",
            Description = "Minimum share volume traded today.",
            Required = false
        },
        ["min_price"] = new()
        {
            Type = "number",
            Description = "Minimum last price. Useful because a one-paisa tick on a Rs 1 stock is a " +
                          "large percentage move that is not actionable.",
            Required = false
        },
        ["include_breadth"] = new()
        {
            Type = "boolean",
            Description = "Include session breadth (advancing/declining/at-cap counts, turnover " +
                          "split). Defaults to true — it is free, from the same snapshot.",
            Required = false
        },
        ["include_sectors"] = new()
        {
            Type = "boolean",
            Description = "Include sector rotation (median change and turnover per sector). " +
                          "Defaults to false.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        if (!_client.Enabled)
        {
            return ToolResult.Fail(
                "The AHL analytics portal is disabled. Set Plugins:AhlAnalytics:Enabled to true to " +
                "use market-wide screens.");
        }

        var screenArg = ToolArgs.Text(arguments, "screen") ?? "gainers";
        var screen = AhlMovers.ParseScreen(screenArg);
        if (screen is null)
        {
            return ToolResult.Fail(
                $"Unknown screen '{screenArg}'. Valid screens: {string.Join(", ", AhlMovers.ScreenNames)}.");
        }

        var limit = ToolArgs.Int(arguments, "limit") ?? 10;

        var filter = new AhlMovers.Filter(
            Index: ToolArgs.Text(arguments, "index"),
            SectorCode: ToolArgs.Text(arguments, "sector_code"),
            MinTurnoverPkr: ToolArgs.Decimal(arguments, "min_turnover_pkr"),
            MinVolume: ToolArgs.Long(arguments, "min_volume"),
            MinPrice: ToolArgs.Decimal(arguments, "min_price"));

        var snapshot = await _client.GetMarketSnapshotAsync();
        if (snapshot is null)
        {
            // Report the upstream reason rather than a guess: an agent that is told "no broker
            // session" when the truth is "the broker's endpoint is 500ing" will retry pointlessly.
            return ToolResult.Fail(
                "Could not read the market snapshot from the analytics portal. " +
                (_client.LastError ?? "No further detail was reported."));
        }

        var rows = AhlMovers.Run(snapshot, screen.Value, limit, filter);

        if (rows.Count == 0)
        {
            // Distinguish "nothing matched your filters" from "the market has not traded", because the
            // remedies are opposite and the empty list looks identical.
            var anyFresh = AhlMovers.MarketBreadth(snapshot)?.TradedToday ?? 0;
            var reason = anyFresh == 0
                ? $"no symbol has traded in the session ending {snapshot.LastUpdate} " +
                  $"(market state {snapshot.MarketState})"
                : "no symbol passed the supplied filters";
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                screen = screen.Value.ToString(),
                market_state = snapshot.MarketState,
                as_of = snapshot.LastUpdate,
                rows = Array.Empty<object>(),
                note = $"Empty result: {reason}."
            }, JsonOptions));
        }

        var includeBreadth = ToolArgs.Bool(arguments, "include_breadth") ?? true;
        var includeSectors = ToolArgs.Bool(arguments, "include_sectors") ?? false;

        var payload = new
        {
            screen = screen.Value.ToString(),
            market_state = snapshot.MarketState,
            as_of = snapshot.LastUpdate,
            // State the filter back, so a surprising list can be read against what produced it.
            filters = new
            {
                index = filter.Index,
                sector_code = filter.SectorCode,
                min_turnover_pkr = filter.MinTurnoverPkr,
                min_price = filter.MinPrice
            },
            rows = rows.Select(r => new
            {
                r.Symbol,
                r.Name,
                r.Sector,
                price = r.Price,
                change = r.Change,
                change_percent = r.ChangePercent,
                volume = r.Volume,
                turnover_pkr = r.TurnoverPkr,
                turnover = AhlMovers.FormatPkr(r.TurnoverPkr),
                volume_vs_avg_10d = r.VolumeVsAvg10Day,
                gap_percent = r.GapPercent,
                rsi = r.Rsi,
                distance_to_upper_cap_percent = r.DistanceToUpperCapPercent,
                distance_to_lower_lock_percent = r.DistanceToLowerLockPercent,
                at_upper_cap = r.AtUpperCap,
                at_lower_lock = r.AtLowerLock,
                free_float = r.FreeFloat,
                dividend_yield_percent = r.DividendYieldPercent,
                indices = r.Indices,
                // Surfaced on every row because a mover that is simply trading ex-dividend is not a
                // mover at all — the price dropped by the payout, mechanically.
                ex_dividend = r.ExDividend,
                ex_bonus = r.ExBonus,
                ex_rights = r.ExRights
            }),
            breadth = includeBreadth ? AhlMovers.MarketBreadth(snapshot) : null,
            sectors = includeSectors ? AhlMovers.SectorRotation(snapshot, filter).Take(15) : null
        };

        _logger.LogDebug("[MarketMovers] {Screen} returned {Count} rows.", screen, rows.Count);
        return ToolResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
