using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TradingAgent.Market;

namespace TradingAgent.Research;

/// <summary>
/// Daily OHLC candles and live quotes from the PSX data portal.
///
/// The portal publishes two market-wide tables, and that shape drives the whole design:
///   POST /historical  (form field date=yyyy-MM-dd) — settled OHLC for EVERY symbol on one date
///   GET  /market-watch                            — the live forming bar for EVERY symbol
///
/// So candle history for a watchlist costs one request per trading DAY, not per symbol-day: a
/// 40-symbol, 60-day scan is ~68 requests, and the same 68 serve a 400-symbol scan. Settled dates
/// are immutable, so each is cached for the process lifetime (bounded by
/// <c>Scan.MaxCachedMarketDays</c>) and only the newest session is ever fetched again. The live
/// table is cached for <c>Scan.MarketWatchCacheSeconds</c>.
///
/// Both tables are HTML, and every fetch here is fail-soft the same way as the rest of this class:
/// an unreachable date degrades the history and is reported as a warning, it never throws out of
/// <see cref="GetCandleHistoryAsync"/>.
/// </summary>
public sealed partial class PsxDataClient
{
    /// <summary>Settled sessions, keyed by trading date. Only non-empty results stay cached.</summary>
    private readonly ConcurrentDictionary<DateOnly, Lazy<Task<IReadOnlyDictionary<string, PsxCandle>>>> _marketDays = new();

    /// <summary>
    /// Dates the portal answered with no rows, and when. A non-trading day and a throttled request
    /// are INDISTINGUISHABLE in the response — the portal serves both as HTTP 200 with an empty
    /// table — so an empty result is remembered only briefly. Caching it permanently is what silently
    /// turns a rate-limited warm-up into a scan running on a third of the intended history.
    /// </summary>
    private readonly ConcurrentDictionary<DateOnly, DateTime> _emptyDays = new();

    private static readonly TimeSpan EmptyDayRetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Caps concurrent portal requests during a cold-cache warm-up. Created lazily because the
    /// permit count comes from options, which are not yet assigned when field initializers run.
    /// </summary>
    private readonly Lazy<SemaphoreSlim> _marketDayGate;

    private readonly SemaphoreSlim _marketWatchGate = new(1, 1);
    private IReadOnlyDictionary<string, PsxLiveQuote>? _marketWatch;
    private DateTime _marketWatchAtUtc;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads daily candles for <paramref name="symbols"/> over the most recent
    /// <paramref name="tradingDays"/> settled sessions, optionally topped up with the live forming
    /// bar. Series are oldest-first. Symbols the feed does not cover come back absent from
    /// <see cref="CandleHistory.Series"/> with a warning rather than as an empty series, so callers
    /// can tell "no data" from "no trades".
    /// </summary>
    public async Task<CandleHistory> GetCandleHistoryAsync(
        IEnumerable<string> symbols,
        int tradingDays,
        bool includeLive = true,
        CancellationToken ct = default)
    {
        var wanted = symbols
            .Select(s => s?.Trim().ToUpperInvariant() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        tradingDays = Math.Clamp(tradingDays, 5, 250);
        var warnings = new List<string>();
        var today = PsxTime.Today();

        // Weekends are never trading days, so only weekdays are candidate sessions. Ask for a
        // margin above the target so public holidays inside the window do not shorten the history.
        var candidates = WeekdaysBefore(today, tradingDays + Math.Max(8, tradingDays / 5));

        var days = new List<(DateOnly Date, IReadOnlyDictionary<string, PsxCandle> Rows)>();
        var fetches = candidates
            .Select(async date =>
            {
                try
                {
                    return (Date: date, Rows: await GetMarketDayAsync(date, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[PsxCandles] Market day {Date} could not be loaded.", date);
                    lock (warnings)
                        warnings.Add($"Trading day {date:yyyy-MM-dd} could not be loaded: {ex.Message}");
                    return (Date: date, Rows: (IReadOnlyDictionary<string, PsxCandle>)new Dictionary<string, PsxCandle>());
                }
            })
            .ToList();

        foreach (var result in await Task.WhenAll(fetches))
        {
            if (result.Rows.Count > 0)
                days.Add(result);
        }

        // Newest-first, keep the requested window, then flip to oldest-first for the analyzers.
        var sessions = days
            .OrderByDescending(d => d.Date)
            .Take(tradingDays)
            .OrderBy(d => d.Date)
            .ToList();

        // Holidays make a small shortfall normal; a large one means the portal served empty tables
        // (it does that under load), and the caller must know its levels came from a shorter window
        // than configured rather than assuming the full lookback was honoured.
        if (sessions.Count < tradingDays * 0.9m)
            warnings.Add(
                $"Only {sessions.Count} of the {tradingDays} requested trading sessions were returned by " +
                "the PSX historical feed (market holidays, or the portal serving empty responses under " +
                "load). Levels are drawn from the shorter window — re-run in a few minutes for the full one.");

        var live = includeLive
            ? await GetMarketWatchSafeAsync(warnings, ct)
            : new Dictionary<string, PsxLiveQuote>();

        var series = new Dictionary<string, IReadOnlyList<PsxCandle>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in wanted)
        {
            var bars = new List<PsxCandle>(sessions.Count);
            foreach (var session in sessions)
            {
                if (session.Rows.TryGetValue(symbol, out var candle))
                    bars.Add(candle);
            }

            // The live bar replaces a same-day settled bar (its range is at least as current) and is
            // otherwise appended. Without this the scanner would judge "at support right now" from
            // yesterday's close during an open session.
            if (live.TryGetValue(symbol, out var quote) && quote.ToCandle(today) is { } forming)
            {
                if (bars.Count > 0 && bars[^1].Date == forming.Date)
                    bars[^1] = forming;
                else if (bars.Count == 0 || bars[^1].Date < forming.Date)
                    bars.Add(forming);
            }

            if (bars.Count == 0)
            {
                warnings.Add($"{symbol}: the PSX market summary returned no rows — verify the ticker is listed.");
                continue;
            }

            series[symbol] = bars;
        }

        TrimMarketDayCache();

        return new CandleHistory
        {
            Series         = series,
            Live           = live,
            Sessions       = sessions.Select(s => s.Date).ToList(),
            RetrievedAtUtc = DateTime.UtcNow,
            Warnings       = warnings
        };
    }

    /// <summary>
    /// Settled OHLC for every symbol traded on <paramref name="date"/>, keyed by ticker. An empty
    /// map means the exchange published nothing for that date (weekend, holiday, or a session that
    /// has not settled yet) and is cached as such.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PsxCandle>> GetMarketDayAsync(
        DateOnly date, CancellationToken ct = default)
    {
        if (_emptyDays.TryGetValue(date, out var observedUtc))
        {
            if (DateTime.UtcNow - observedUtc < EmptyDayRetryAfter)
                return new Dictionary<string, PsxCandle>();

            _emptyDays.TryRemove(date, out _);
        }

        var entry = _marketDays.GetOrAdd(date, d =>
            new Lazy<Task<IReadOnlyDictionary<string, PsxCandle>>>(
                () => FetchMarketDayAsync(d), LazyThreadSafetyMode.ExecutionAndPublication));

        IReadOnlyDictionary<string, PsxCandle> rows;
        try
        {
            // The shared task is deliberately not bound to any one caller's token; a caller that
            // gives up must not cancel the fetch the other callers are waiting on.
            rows = await entry.Value.WaitAsync(ct);
        }
        catch
        {
            // A failed fetch must never become a permanent negative cache entry.
            _marketDays.TryRemove(new KeyValuePair<DateOnly, Lazy<Task<IReadOnlyDictionary<string, PsxCandle>>>>(date, entry));
            throw;
        }

        if (rows.Count == 0)
        {
            // Might be a holiday, might be the portal refusing under load. Remember it only for
            // EmptyDayRetryAfter so a genuine holiday is not refetched on every scan while a
            // throttled response still heals on its own.
            _marketDays.TryRemove(new KeyValuePair<DateOnly, Lazy<Task<IReadOnlyDictionary<string, PsxCandle>>>>(date, entry));
            _emptyDays[date] = DateTime.UtcNow;
        }

        return rows;
    }

    /// <summary>
    /// Live quotes for the whole market in one request, cached briefly. Includes the session's
    /// open/high/low so far, the last trade, and volume.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PsxLiveQuote>> GetMarketWatchAsync(CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromSeconds(Math.Clamp(_options.Value.Scan.MarketWatchCacheSeconds, 5, 900));
        if (_marketWatch is { } cached && DateTime.UtcNow - _marketWatchAtUtc < ttl)
            return cached;

        await _marketWatchGate.WaitAsync(ct);
        try
        {
            if (_marketWatch is { } fresh && DateTime.UtcNow - _marketWatchAtUtc < ttl)
                return fresh;

            var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
            var html = await _http.GetStringAsync($"{baseUrl}/market-watch", ct);
            var rows = ParseMarketWatchTable(html, DateTime.UtcNow);

            _logger.LogDebug("[PsxCandles] Market watch returned {Count} symbols.", rows.Count);

            // Keep the previous snapshot if a fetch parses to nothing — a layout change should
            // degrade to a slightly stale live bar, not to no live bar at all.
            if (rows.Count == 0 && _marketWatch is { Count: > 0 } previous)
                return previous;

            _marketWatch = rows;
            _marketWatchAtUtc = DateTime.UtcNow;
            return rows;
        }
        finally
        {
            _marketWatchGate.Release();
        }
    }

    /// <summary>Source URLs consulted by the candle layer, for citation.</summary>
    public IReadOnlyList<string> CandleSourceUrls()
    {
        var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
        return [$"{baseUrl}/historical", $"{baseUrl}/market-watch"];
    }

    // ── Intraday candles ──────────────────────────────────────────────────────

    /// <summary>
    /// Every executed trade of the CURRENT session for one symbol, oldest first.
    ///
    /// The portal serves the complete tick tape here — for a liquid symbol several thousand trades a
    /// few seconds apart, whose quantities sum exactly to the day's published volume — which is what
    /// makes true intraday candles possible at any bucket width. The hard limit is that only the
    /// current session is available: the endpoint ignores a date parameter, and PSX publishes no
    /// historical intraday anywhere. Past sessions therefore come from the local archive
    /// (<see cref="TradingAgent.Persistence.ITradingRepository.GetIntradayBarsAsync"/>), which is why
    /// intraday history accumulates from the day archiving is switched on rather than existing up front.
    /// </summary>
    public async Task<IReadOnlyList<PsxTick>> GetIntradayTicksAsync(
        string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeStockSymbol(symbol);
        var points = await FetchSeriesAsync($"timeseries/int/{symbol}", ct);

        return points
            .Where(p => p.Price > 0)
            .Select(p => new PsxTick(
                DateTimeOffset.FromUnixTimeSeconds(p.Ts).UtcDateTime,
                p.Price,
                Math.Max(0, p.Volume)))
            .OrderBy(t => t.TimeUtc)
            .ToList();
    }

    /// <summary>
    /// Buckets ticks into OHLCV candles of <paramref name="intervalMinutes"/> width. Pure and
    /// deterministic so bucketing is unit-tested rather than trusted.
    ///
    /// Buckets are aligned to the epoch, which lands them on clean PKT wall-clock boundaries because
    /// the exchange's offset (UTC+5, no DST) is a whole number of hours. The final bucket is flagged
    /// <see cref="PsxCandle.IsLive"/> while its window has not elapsed: the analyzer wants that
    /// forming bar, and the archive must not persist it as if it were complete.
    /// </summary>
    public static IReadOnlyList<PsxCandle> AggregateTicks(
        string symbol,
        IReadOnlyList<PsxTick> ticks,
        int intervalMinutes,
        DateTime? nowUtc = null)
    {
        if (ticks.Count == 0 || intervalMinutes <= 0) return [];

        symbol = symbol.Trim().ToUpperInvariant();
        var now = nowUtc ?? DateTime.UtcNow;
        var width = intervalMinutes * 60L;
        var bars = new List<PsxCandle>();

        foreach (var group in ticks
            .OrderBy(t => t.TimeUtc)
            .GroupBy(t => new DateTimeOffset(t.TimeUtc, TimeSpan.Zero).ToUnixTimeSeconds() / width * width)
            .OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var start = DateTimeOffset.FromUnixTimeSeconds(group.Key).UtcDateTime;

            bars.Add(new PsxCandle
            {
                Symbol          = symbol,
                Date            = DateOnly.FromDateTime(PsxTime.Now(start)),
                BucketStartUtc  = start,
                IntervalMinutes = intervalMinutes,
                Open            = ordered[0].Price,
                High            = ordered.Max(t => t.Price),
                Low             = ordered.Min(t => t.Price),
                Close           = ordered[^1].Price,
                Volume          = ordered.Sum(t => t.Quantity),
                IsLive          = start.AddMinutes(intervalMinutes) > now
            });
        }

        return bars;
    }

    /// <summary>Bar widths the tools accept, mapped from their request labels.</summary>
    public static readonly IReadOnlyDictionary<string, int> SupportedIntervals =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1mo"] = CandleInterval.Monthly,
            ["month"] = CandleInterval.Monthly,
            ["monthly"] = CandleInterval.Monthly,
            ["1w"] = CandleInterval.Weekly,
            ["week"] = CandleInterval.Weekly,
            ["weekly"] = CandleInterval.Weekly,
            ["1d"] = PsxCandle.DailyIntervalMinutes,
            ["1day"] = PsxCandle.DailyIntervalMinutes,
            ["daily"] = PsxCandle.DailyIntervalMinutes,
            ["60m"] = 60,
            ["1h"] = 60,
            ["30m"] = 30,
            ["15m"] = 15,
            ["5m"] = 5
        };

    /// <summary>Resolves an interval label to a bar width, or null when it is not supported.</summary>
    public static int? ResolveInterval(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return PsxCandle.DailyIntervalMinutes;

        // Preserve the conventional distinction between 1M (month) and 1m (one minute). One-minute
        // bars are not supported, so silently interpreting a lowercase request as monthly is unsafe.
        var value = label.Trim();
        if (value == "1M") return CandleInterval.Monthly;
        return SupportedIntervals.TryGetValue(value, out var minutes) ? minutes : null;
    }

    /// <summary>Canonical label for a bar width, for output and archive keys.</summary>
    public static string IntervalLabel(int intervalMinutes) => CandleInterval.Label(intervalMinutes);

    // ── Fetching ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches one date straight from the portal, bypassing the in-memory cache. Used by the backfill,
    /// which walks hundreds of dates: routing those through the cache would evict the recent sessions
    /// every scan depends on, to hold history that is being written to the durable archive anyway.
    /// </summary>
    public Task<IReadOnlyDictionary<string, PsxCandle>> FetchMarketDayUncachedAsync(
        DateOnly date, CancellationToken ct = default) => FetchMarketDayAsync(date, ct);

    private async Task<IReadOnlyDictionary<string, PsxCandle>> FetchMarketDayAsync(
        DateOnly date, CancellationToken ct = default)
    {
        var gate = _marketDayGate.Value;
        await gate.WaitAsync(ct);
        try
        {
            var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
            using var form = new FormUrlEncodedContent(
                [new KeyValuePair<string, string>("date", date.ToString("yyyy-MM-dd"))]);
            using var response = await _http.PostAsync($"{baseUrl}/historical", form, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            var rows = ParseHistoricalTable(html, date);
            _logger.LogDebug("[PsxCandles] {Date:yyyy-MM-dd}: {Count} settled rows.", date, rows.Count);
            return rows;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, PsxLiveQuote>> GetMarketWatchSafeAsync(
        List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await GetMarketWatchAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[PsxCandles] Market watch fetch failed.");
            warnings.Add($"Live market watch unavailable ({ex.Message}); analysis uses settled closes only.");
            return new Dictionary<string, PsxLiveQuote>();
        }
    }

    /// <summary>
    /// Drops the oldest cached sessions once the cache exceeds its configured size. Each session
    /// holds a row per traded symbol (~600), so an unbounded cache would grow with every scan window.
    /// </summary>
    private void TrimMarketDayCache()
    {
        var max = Math.Clamp(_options.Value.Scan.MaxCachedMarketDays, 20, 500);
        if (_marketDays.Count <= max) return;

        foreach (var date in _marketDays.Keys.OrderBy(d => d).Take(_marketDays.Count - max))
            _marketDays.TryRemove(date, out _);
    }

    /// <summary>Weekday dates strictly at or before <paramref name="asOf"/>, newest first.</summary>
    private static List<DateOnly> WeekdaysBefore(DateOnly asOf, int count)
    {
        var dates = new List<DateOnly>(count);
        var cursor = asOf;
        // Guard the walk independently of `count` so a pathological input cannot loop unbounded.
        for (var step = 0; dates.Count < count && step < count * 3 + 30; step++, cursor = cursor.AddDays(-1))
        {
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(cursor);
        }
        return dates;
    }

    // ── Parsing (pure — unit-tested against saved portal markup) ───────────────

    /// <summary>
    /// Projects the portal's historical market summary into settled candles keyed by ticker.
    /// Rows missing any of open/high/low/close are dropped: a partial bar is worse than no bar,
    /// because the analyzers would read a zero as a real price.
    /// </summary>
    public static IReadOnlyDictionary<string, PsxCandle> ParseHistoricalTable(string? html, DateOnly date)
    {
        var result = new Dictionary<string, PsxCandle>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in ParseDataTable(html))
        {
            if (!row.TryGetValue("symbol", out var symbol) || symbol.Length == 0) continue;

            var open  = Number(row, "open");
            var high  = Number(row, "high");
            var low   = Number(row, "low");
            var close = Number(row, "close");
            if (open is not > 0 || high is not > 0 || low is not > 0 || close is not > 0) continue;

            result[symbol] = new PsxCandle
            {
                Symbol        = symbol,
                Date          = date,
                Open          = open.Value,
                High          = Math.Max(high.Value, Math.Max(open.Value, close.Value)),
                Low           = Math.Min(low.Value, Math.Min(open.Value, close.Value)),
                Close         = close.Value,
                PreviousClose = Number(row, "ldcp"),
                Volume        = (long?)Number(row, "volume") ?? 0
            };
        }

        return result;
    }

    /// <summary>
    /// Projects the portal's market watch into live quotes keyed by ticker. The table's "close"
    /// column is the CURRENT last-traded price during a session, not a settled close.
    /// </summary>
    public static IReadOnlyDictionary<string, PsxLiveQuote> ParseMarketWatchTable(
        string? html, DateTime retrievedAtUtc)
    {
        var result = new Dictionary<string, PsxLiveQuote>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in ParseDataTable(html))
        {
            if (!row.TryGetValue("symbol", out var symbol) || symbol.Length == 0) continue;

            result[symbol] = new PsxLiveQuote
            {
                Symbol         = symbol,
                CompanyName    = row.GetValueOrDefault("companyName"),
                Sector         = row.GetValueOrDefault("sector"),
                PreviousClose  = Number(row, "ldcp"),
                Open           = Number(row, "open"),
                High           = Number(row, "high"),
                Low            = Number(row, "low"),
                Current        = Number(row, "close"),
                ChangePercent  = Number(row, "percentChange"),
                Volume         = (long?)Number(row, "volume"),
                RetrievedAtUtc = retrievedAtUtc
            };
        }

        return result;
    }

    // Both portal tables tag their header cells with data-name (symbol, ldcp, open, high, low,
    // close, change, percentChange, volume), so columns are read by NAME rather than by position —
    // a reordered or newly inserted column then cannot silently shift prices into the wrong field.
    private static readonly Regex TableRowRegex =
        new(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TableCellRegex =
        new(@"<t(?<tag>[dh])(?<attrs>[^>]*)>(?<body>.*?)</t\k<tag>>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HeaderNameRegex =
        new(@"data-name=""(?<name>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CellValueRegex =
        new(@"data-(?:value|order)=""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex CompanyTitleRegex =
        new(@"data-title=""(?<value>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Reads a portal table into one dictionary per body row, keyed by the header's data-name.
    /// Cell values prefer the row's machine-readable data-value/data-order attribute and fall back
    /// to the visible text. Returns an empty list when no header mapping can be established, which
    /// is how a layout change surfaces as "no data" instead of as garbage.
    /// </summary>
    private static List<Dictionary<string, string>> ParseDataTable(string? html)
    {
        var rows = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(html)) return rows;

        string[]? columns = null;

        foreach (var rowMatch in TableRowRegex.Matches(html).Cast<Match>())
        {
            var cells = TableCellRegex.Matches(rowMatch.Groups["row"].Value).Cast<Match>().ToList();
            if (cells.Count == 0) continue;

            // Header row: build the column map and move on.
            if (cells[0].Groups["tag"].Value == "h")
            {
                var names = cells
                    .Select(c => HeaderNameRegex.Match(c.Groups["attrs"].Value) is { Success: true } m
                        ? m.Groups["name"].Value
                        : "")
                    .ToArray();
                if (names.Any(n => n.Length > 0))
                    columns = names;
                continue;
            }

            if (columns is null) continue;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < cells.Count && i < columns.Length; i++)
            {
                var name = columns[i];
                if (name.Length == 0) continue;

                var cell = cells[i];
                var value = CellValueRegex.Match(cell.Groups["attrs"].Value) is { Success: true } attr
                    ? attr.Groups["value"].Value
                    : CellText(cell.Groups["body"].Value);

                row[name] = value.Trim();

                // The symbol cell's machine value is the ticker, while the nested link carries the
                // human issuer name. Preserve both instead of making the UI maintain a stale symbol map.
                if (name.Equals("symbol", StringComparison.OrdinalIgnoreCase)
                    && CompanyTitleRegex.Match(cell.Groups["body"].Value) is { Success: true } title)
                {
                    row["companyName"] = WebUtility.HtmlDecode(title.Groups["value"].Value).Trim();
                }
            }

            if (row.Count > 0) rows.Add(row);
        }

        return rows;
    }

    private static string CellText(string body) =>
        WebUtility.HtmlDecode(TagRegex.Replace(body, " ")).Trim();

    /// <summary>
    /// Reads one numeric column. Thousands separators, a percent sign, and the portal's directional
    /// arrow glyphs are stripped; anything unparseable reads as null (unknown), never as zero.
    /// </summary>
    private static decimal? Number(IReadOnlyDictionary<string, string> row, string column)
    {
        if (!row.TryGetValue(column, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = raw.Replace(",", "").Replace("%", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
