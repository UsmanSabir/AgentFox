using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingAgent.AhlAnalytics;

// The AHL research portal names every snapshot field with one to four letters — `pch`, `va10d`,
// `ldcp`, `bt`. Those names are load-bearing on the wire and unreadable at a call site, so every
// DTO here maps them once, via [JsonPropertyName], to a name that says what the number is. The
// mapping was taken from the portal's own `cs.market.Mappings` key table where it had one, and
// resolved against `company-statement` where the table was absent or wrong.
//
// Scaling trap, worth stating because it is silent: `pm` and `di` arrive as PERCENTAGES (34.15,
// 1.06) while the same quantities in `company-statement` are FRACTIONS (0.3415, 0.0106). They are
// named ...Percent here so nothing multiplies by 100 twice.

/// <summary>
/// The whole-market snapshot from <c>POST /api/v3/market?path=/req</c> (body <c>item=market</c>) —
/// one ~1.1 MB call carrying every listed equity, index, future and ETF.
/// </summary>
public sealed class AhlMarketSnapshot
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("data")]   public AhlSnapshotData? Data { get; set; }
}

public sealed class AhlSnapshotData
{
    /// <summary>Market state: <c>"OPN"</c> open, <c>"SUS"</c> suspended/closed, and others.</summary>
    [JsonPropertyName("st")] public string? MarketState { get; set; }

    /// <summary>
    /// Timestamp the market data was last updated, <c>"yyyy-MM-dd HH:mm:ss"</c>. This is the
    /// reference every staleness check compares against — see <see cref="AhlEquity.LastTickAt"/>.
    /// </summary>
    [JsonPropertyName("lu")] public string? LastUpdate { get; set; }

    [JsonPropertyName("eq")]  public Dictionary<string, AhlEquity>? Equities { get; set; }
    [JsonPropertyName("in")]  public Dictionary<string, AhlIndex>? Indices { get; set; }
    [JsonPropertyName("fut")] public Dictionary<string, AhlDerivative>? Futures { get; set; }
    [JsonPropertyName("odl")] public Dictionary<string, AhlDerivative>? OddLot { get; set; }
}

/// <summary>
/// One equity in the market snapshot. Everything the portal knows about a symbol without a
/// per-symbol request — which is what makes a whole-market screen one call instead of 857.
/// </summary>
public sealed class AhlEquity
{
    // ── identity ──────────────────────────────────────────────────────────────
    /// <summary>Last tick time, <c>"yyyy-MM-dd HH:mm:ss"</c>. Compare its DATE against
    /// <see cref="AhlSnapshotData.LastUpdate"/> to tell a symbol that traded today from one that has
    /// been dormant for months — the snapshot contains both, indistinguishable by price alone.</summary>
    [JsonPropertyName("d")]  public string? LastTickAt { get; set; }
    [JsonPropertyName("nm")] public string? Name { get; set; }
    /// <summary>PSX sector code, e.g. <c>"0804"</c> Cement. <see cref="AhlSectors"/> resolves names.</summary>
    [JsonPropertyName("sc")] public string? SectorCode { get; set; }
    [JsonPropertyName("st")] public int? State { get; set; }
    [JsonPropertyName("ty")] public int? Type { get; set; }

    // ── price / volume ────────────────────────────────────────────────────────
    [JsonPropertyName("o")]    public decimal? Open { get; set; }
    [JsonPropertyName("h")]    public decimal? High { get; set; }
    [JsonPropertyName("l")]    public decimal? Low { get; set; }
    [JsonPropertyName("c")]    public decimal? Close { get; set; }
    [JsonPropertyName("v")]    public long? Volume { get; set; }
    [JsonPropertyName("ch")]   public decimal? Change { get; set; }
    /// <summary>Change as a FRACTION (-0.0048 = -0.48%), unlike the percent-scaled ratios below.</summary>
    [JsonPropertyName("pch")]  public decimal? ChangeFraction { get; set; }
    [JsonPropertyName("avg")]  public decimal? AveragePrice { get; set; }
    [JsonPropertyName("tr")]   public long? TradeCount { get; set; }
    /// <summary>Previous session's close — the reference for a gap, since <see cref="Open"/> is today's.</summary>
    [JsonPropertyName("ldcp")] public decimal? PreviousClose { get; set; }
    [JsonPropertyName("ldcv")] public long? PreviousVolume { get; set; }

    // ── L1 book ───────────────────────────────────────────────────────────────
    // Best bid/ask only; this portal has no L2 ladder. All four are 0 outside market hours.
    [JsonPropertyName("bidp")] public decimal? BidPrice { get; set; }
    [JsonPropertyName("bidv")] public long? BidVolume { get; set; }
    [JsonPropertyName("askp")] public decimal? AskPrice { get; set; }
    [JsonPropertyName("askv")] public long? AskVolume { get; set; }

    // ── circuit breakers ──────────────────────────────────────────────────────
    /// <summary>Upper price cap for the session. A symbol AT this cannot be bought higher.</summary>
    [JsonPropertyName("uc")]  public decimal? UpperCap { get; set; }
    /// <summary>Lower lock for the session.</summary>
    [JsonPropertyName("lc")]  public decimal? LowerLock { get; set; }
    [JsonPropertyName("var")] public decimal? BandPercent { get; set; }
    [JsonPropertyName("hc")]  public decimal? HaircutPercent { get; set; }

    // ── ranges ────────────────────────────────────────────────────────────────
    [JsonPropertyName("h52")] public decimal? High52Week { get; set; }
    [JsonPropertyName("l52")] public decimal? Low52Week { get; set; }

    // ── float ─────────────────────────────────────────────────────────────────
    [JsonPropertyName("sh")] public decimal? SharesOutstanding { get; set; }
    /// <summary>Free float in shares. The honest denominator for "is my order size sane here".</summary>
    [JsonPropertyName("ff")] public decimal? FreeFloat { get; set; }

    // ── technicals (portal-computed, daily) ───────────────────────────────────
    [JsonPropertyName("rsi")] public decimal? Rsi { get; set; }
    [JsonPropertyName("std")] public decimal? StdDev { get; set; }
    [JsonPropertyName("pp")]  public AhlPivotPoints? PivotPoints { get; set; }
    // The portal normally sends an object, but currently sends `false` for a few symbols (for
    // example ADOS). A missing optional beta must not make the entire market snapshot unreadable.
    [JsonPropertyName("bt")]
    [JsonConverter(typeof(AhlBetaConverter))]
    public AhlBeta? Beta { get; set; }

    // ── fundamentals ──────────────────────────────────────────────────────────
    [JsonPropertyName("eps")] public decimal? Eps { get; set; }
    [JsonPropertyName("dps")] public decimal? Dps { get; set; }
    /// <summary>Net profit margin as a PERCENT (34.15 = 34.15%).</summary>
    [JsonPropertyName("pm")]  public decimal? NetMarginPercent { get; set; }
    /// <summary>Dividend yield as a PERCENT (1.06 = 1.06%).</summary>
    [JsonPropertyName("di")]  public decimal? DividendYieldPercent { get; set; }
    /// <summary>
    /// A P/E-like ratio whose basis could not be established. The vendor's own key table calls it
    /// <c>profit_margin</c>, which it is not; for LUCK it was 15.71 against a <c>pe_ratio</c> of
    /// 14.75 and a <c>close/eps</c> of 13.74. <b>Do not present this as P/E</b> — take that from
    /// <c>company-statement</c>'s <c>pe_ratio</c>, which reconciles.
    /// </summary>
    [JsonPropertyName("pr")]  public decimal? UnverifiedPriceRatio { get; set; }
    [JsonPropertyName("sa")]  public decimal? Sales { get; set; }
    [JsonPropertyName("pat")] public decimal? ProfitAfterTax { get; set; }
    [JsonPropertyName("as")]  public decimal? TotalAssets { get; set; }
    [JsonPropertyName("sg3y")]     public decimal? SalesGrowth3Y { get; set; }
    [JsonPropertyName("scagr5y")]  public decimal? SalesCagr5Y { get; set; }
    [JsonPropertyName("pcagr5y")]  public decimal? ProfitCagr5Y { get; set; }
    [JsonPropertyName("eg3y")]     public decimal? EpsGrowth3Y { get; set; }

    // ── historic prices ───────────────────────────────────────────────────────
    [JsonPropertyName("p1w")]  public decimal? Price1WeekAgo { get; set; }
    [JsonPropertyName("p1m")]  public decimal? Price1MonthAgo { get; set; }
    [JsonPropertyName("p3m")]  public decimal? Price3MonthsAgo { get; set; }
    [JsonPropertyName("p6m")]  public decimal? Price6MonthsAgo { get; set; }
    [JsonPropertyName("p1y")]  public decimal? Price1YearAgo { get; set; }
    [JsonPropertyName("pytd")] public decimal? PriceYearStart { get; set; }
    [JsonPropertyName("p5y")]  public decimal? Price5YearsAgo { get; set; }

    // ── average volumes ───────────────────────────────────────────────────────
    /// <summary>10-day average volume — the liquidity denominator for an unusual-volume screen.</summary>
    [JsonPropertyName("va10d")] public decimal? AvgVolume10Day { get; set; }
    [JsonPropertyName("vam")]   public decimal? AvgVolumeMonth { get; set; }
    [JsonPropertyName("va3m")]  public decimal? AvgVolume3Month { get; set; }
    [JsonPropertyName("v30a")]  public decimal? AvgVolume30Session { get; set; }

    // ── corporate action flags ────────────────────────────────────────────────
    [JsonPropertyName("xb")] public bool? ExBonus { get; set; }
    [JsonPropertyName("xd")] public bool? ExDividend { get; set; }
    [JsonPropertyName("xr")] public bool? ExRights { get; set; }
    [JsonPropertyName("sd")] public bool? SpreadFlag { get; set; }

    /// <summary>Indices this symbol belongs to, e.g. <c>["KSE100","KMI30"]</c>.</summary>
    [JsonPropertyName("li")] public List<string>? ListedIn { get; set; }
}

public sealed class AhlPivotPoints
{
    [JsonPropertyName("pp")] public decimal? Pivot { get; set; }
    [JsonPropertyName("r1")] public decimal? R1 { get; set; }
    [JsonPropertyName("r2")] public decimal? R2 { get; set; }
    [JsonPropertyName("r3")] public decimal? R3 { get; set; }
    [JsonPropertyName("s1")] public decimal? S1 { get; set; }
    [JsonPropertyName("s2")] public decimal? S2 { get; set; }
    [JsonPropertyName("s3")] public decimal? S3 { get; set; }
}

public sealed class AhlBeta
{
    [JsonPropertyName("1m")] public decimal? OneMonth { get; set; }
    [JsonPropertyName("3m")] public decimal? ThreeMonth { get; set; }
    [JsonPropertyName("6m")] public decimal? SixMonth { get; set; }
    [JsonPropertyName("1y")] public decimal? OneYear { get; set; }
}

internal sealed class AhlBetaConverter : JsonConverter<AhlBeta?>
{
    public override AhlBeta? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            using var ignored = JsonDocument.ParseValue(ref reader);
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new AhlBeta
        {
            OneMonth = ReadDecimal(root, "1m"),
            ThreeMonth = ReadDecimal(root, "3m"),
            SixMonth = ReadDecimal(root, "6m"),
            OneYear = ReadDecimal(root, "1y")
        };
    }

    public override void Write(Utf8JsonWriter writer, AhlBeta? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);

    private static decimal? ReadDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}

public sealed class AhlIndex
{
    [JsonPropertyName("d")]    public string? LastTickAt { get; set; }
    [JsonPropertyName("o")]    public decimal? Open { get; set; }
    [JsonPropertyName("h")]    public decimal? High { get; set; }
    [JsonPropertyName("l")]    public decimal? Low { get; set; }
    [JsonPropertyName("c")]    public decimal? Close { get; set; }
    [JsonPropertyName("v")]    public long? Volume { get; set; }
    [JsonPropertyName("val")]  public decimal? Turnover { get; set; }
    [JsonPropertyName("ch")]   public decimal? Change { get; set; }
    [JsonPropertyName("pch")]  public decimal? ChangeFraction { get; set; }
    [JsonPropertyName("ldci")] public decimal? PreviousClose { get; set; }
    [JsonPropertyName("h52")]  public decimal? High52Week { get; set; }
    [JsonPropertyName("l52")]  public decimal? Low52Week { get; set; }
    [JsonPropertyName("pp")]   public AhlPivotPoints? PivotPoints { get; set; }
}

/// <summary>A futures or odd-lot contract. <see cref="Underlying"/> maps it back to the spot symbol.</summary>
public sealed class AhlDerivative
{
    [JsonPropertyName("eq")]  public string? Underlying { get; set; }
    [JsonPropertyName("m")]   public string? Market { get; set; }
    [JsonPropertyName("n")]   public string? Name { get; set; }
    [JsonPropertyName("d")]   public string? LastTickAt { get; set; }
    [JsonPropertyName("o")]   public decimal? Open { get; set; }
    [JsonPropertyName("h")]   public decimal? High { get; set; }
    [JsonPropertyName("l")]   public decimal? Low { get; set; }
    [JsonPropertyName("c")]   public decimal? Close { get; set; }
    [JsonPropertyName("v")]   public long? Volume { get; set; }
    [JsonPropertyName("pch")] public decimal? ChangeFraction { get; set; }
    [JsonPropertyName("uc")]  public decimal? UpperCap { get; set; }
    [JsonPropertyName("lc")]  public decimal? LowerLock { get; set; }
    [JsonPropertyName("fut")] public AhlFuturesTerms? Terms { get; set; }
}

public sealed class AhlFuturesTerms
{
    [JsonPropertyName("dd")]  public string? DeliveryDate { get; set; }
    [JsonPropertyName("dm")]  public string? DeliveryMonth { get; set; }
    [JsonPropertyName("ltd")] public string? LastTradeDate { get; set; }
}

// ── candles ───────────────────────────────────────────────────────────────────

/// <summary>Response wrapper for <c>/daily/{sym}</c> and <c>/intraday/{sym}/{range}</c>.</summary>
public sealed class AhlCandleResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    /// <summary>
    /// Bars, <b>newest first</b> — the vendor's ordering, reversed once at the client boundary rather
    /// than at every call site. Note <c>count</c> on the wire is unreliable (0 on the daily endpoint);
    /// the array length is the truth.
    /// </summary>
    [JsonPropertyName("data")] public List<AhlCandle>? Data { get; set; }
}

public sealed class AhlCandle
{
    [JsonPropertyName("date")]   public string? Date { get; set; }
    [JsonPropertyName("open")]   public decimal Open { get; set; }
    [JsonPropertyName("high")]   public decimal High { get; set; }
    [JsonPropertyName("low")]    public decimal Low { get; set; }
    [JsonPropertyName("close")]  public decimal Close { get; set; }
    [JsonPropertyName("volume")] public long Volume { get; set; }
    // `shares` and `value` are on the wire but are ALWAYS 0 on both endpoints, so they are
    // deliberately not surfaced — a nullable zero invites someone to compute turnover from it.
}

// ── company statements ────────────────────────────────────────────────────────

/// <summary>
/// Response for <c>/api/v3/company-statement</c>. The shape is a matrix: <c>Fields[i].Values[j]</c>
/// is field <c>i</c> in period <c>j</c>, so the two arrays must be read together.
/// </summary>
public sealed class AhlStatementResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("data")]   public AhlStatementData? Data { get; set; }
}

public sealed class AhlStatementData
{
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("interval")] public string? Interval { get; set; }
    /// <summary>
    /// Always <c>false</c>. The <c>consolidated</c> query parameter is accepted and IGNORED by the
    /// API, so only unconsolidated statements are available — which for a holding company understates
    /// earnings substantially (LUCK FY26: 46.6bn here vs 89bn consolidated).
    /// </summary>
    [JsonPropertyName("consolidated")] public bool Consolidated { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("periods")] public List<AhlStatementPeriod>? Periods { get; set; }
    [JsonPropertyName("fields")] public List<AhlStatementField>? Fields { get; set; }
    /// <summary>
    /// Sector min/max/median per field key — present on <c>type=fundamentals</c>. This is what lets a
    /// symbol be ranked against its sector without fetching a single peer.
    /// </summary>
    [JsonPropertyName("sector_stats")] public List<AhlSectorStat>? SectorStats { get; set; }
}

public sealed class AhlStatementPeriod
{
    /// <summary>Fiscal year, or the literal <c>"TTM"</c> for the trailing-twelve-month column.</summary>
    [JsonPropertyName("year")] public string? Year { get; set; }
    [JsonPropertyName("quarter")] public string? Quarter { get; set; }
    [JsonPropertyName("period_end")] public string? PeriodEnd { get; set; }
}

public sealed class AhlStatementField
{
    /// <summary>
    /// Stable machine key, e.g. <c>pe_ratio</c>. <b>Null on some rows</b> (the income statement's
    /// "Distribution costs" and "Other charges"), so anything keying on this must fall back to
    /// <see cref="Label"/> or those rows vanish silently.
    /// </summary>
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Values aligned index-for-index with <see cref="AhlStatementData.Periods"/>.</summary>
    [JsonPropertyName("values")] public List<decimal?>? Values { get; set; }
}

public sealed class AhlSectorStat
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("min")] public decimal? Min { get; set; }
    [JsonPropertyName("max")] public decimal? Max { get; set; }
    [JsonPropertyName("median")] public decimal? Median { get; set; }
}

/// <summary>Company profile from <c>type=profile</c> — a flat object, unlike the statement matrix.</summary>
public sealed class AhlProfileResponse
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("data")] public AhlProfile? Data { get; set; }
}

public sealed class AhlProfile
{
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sector_code")] public string? SectorCode { get; set; }
    [JsonPropertyName("sector_name")] public string? SectorName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("website")] public string? Website { get; set; }
    [JsonPropertyName("employees")] public int? Employees { get; set; }
    /// <summary>Fiscal year end, e.g. <c>"June"</c>. Required to read a quarterly result correctly —
    /// "Q2" means different calendar months for a June-end company than a December-end one.</summary>
    [JsonPropertyName("year_end")] public string? YearEnd { get; set; }
    [JsonPropertyName("par_value")] public decimal? ParValue { get; set; }
    [JsonPropertyName("auditors")] public string? Auditors { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
}

// ── announcements, payouts, insiders, news, research ──────────────────────────

/// <summary>
/// One PSX announcement. Serves the per-symbol feed, the payout breakdown, and the market-wide
/// board-meeting and financial-result calendars — the portal returns the same row shape for all.
/// </summary>
public sealed class AhlAnnouncement
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("name")] public string? CompanyName { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    /// <summary>
    /// Pipe-delimited <c>key=value</c> pairs, NOT JSON — e.g.
    /// <c>"datetime=2026-08-25, 11:00 AM|location=Islamabad|agenda=Annual"</c>. Use
    /// <see cref="AhlAnnouncementDetails.Parse"/>; the keys vary by announcement type.
    /// </summary>
    [JsonPropertyName("details")] public string? Details { get; set; }
    [JsonPropertyName("announcementType")] public string? Type { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("postingTime")] public string? PostingTime { get; set; }
    [JsonPropertyName("periodEndDate")] public string? PeriodEnd { get; set; }
    [JsonPropertyName("quarter")] public string? Quarter { get; set; }

    // Payout fields — present on financial-result and payout announcements.
    [JsonPropertyName("dividend")] public string? Dividend { get; set; }
    [JsonPropertyName("bonus")] public string? Bonus { get; set; }
    [JsonPropertyName("rightPrice")] public string? RightPrice { get; set; }
    /// <summary>Ex-date. The single most decision-relevant field here: holding across it changes
    /// the position's economics and the price gaps by the payout.</summary>
    [JsonPropertyName("exDate")] public string? ExDate { get; set; }
    [JsonPropertyName("book_closure_date_from")] public string? BookClosureFrom { get; set; }
    [JsonPropertyName("book_closure_date_to")] public string? BookClosureTo { get; set; }

    // Parsed result figures, when the announcement is a financial result.
    [JsonPropertyName("unconsolidatedSales")] public string? Sales { get; set; }
    [JsonPropertyName("unconsolidatedPat")] public string? ProfitAfterTax { get; set; }
    [JsonPropertyName("unconsolidatedQuarterEps")] public string? QuarterEps { get; set; }

    // Board / shareholder meeting fields.
    [JsonPropertyName("heldDate")] public string? HeldDate { get; set; }
    [JsonPropertyName("heldTime")] public string? HeldTime { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("agenda")] public string? Agenda { get; set; }

    [JsonPropertyName("pdf_id")] public string? PdfUrl { get; set; }
}

/// <summary>Parses the portal's pipe-delimited <c>details</c> string into a dictionary.</summary>
public static class AhlAnnouncementDetails
{
    public static Dictionary<string, string> Parse(string? details)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(details)) return result;

        foreach (var pair in details.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return result;
    }
}

/// <summary>One insider transaction from <c>/insider-transaction/api</c>.</summary>
public sealed class AhlInsiderTransaction
{
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("company_name")] public string? CompanyName { get; set; }
    [JsonPropertyName("name")] public string? PersonName { get; set; }
    /// <summary>Role, e.g. <c>"Executive"</c>, <c>"Senior Management"</c>, <c>"Non-Executive Director"</c>.</summary>
    [JsonPropertyName("description")] public string? Role { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    /// <summary>Date the trade was DEALT.</summary>
    [JsonPropertyName("date")] public string? DealtDate { get; set; }
    /// <summary>Date the trade was DISCLOSED — days after <see cref="DealtDate"/>. This is the one to
    /// key signals off, because it is when the information actually became public.</summary>
    [JsonPropertyName("notice_date")] public string? NoticeDate { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("shares")] public long? Shares { get; set; }
    [JsonPropertyName("market")] public string? Market { get; set; }
}

/// <summary>One news item from <c>POST /api/v3/news/{symbol|GENERIC}</c>.</summary>
public sealed class AhlNewsResponse
{
    [JsonPropertyName("data")] public List<AhlNewsItem>? Data { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
}

public sealed class AhlNewsItem
{
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
}

/// <summary>
/// One AHL analyst note from <c>/client-research-v2/data/list</c> — the broker's own sell-side
/// research, with the full body inline rather than only a PDF link.
/// </summary>
public sealed class AhlResearchNote
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("tt")] public string? Title { get; set; }
    [JsonPropertyName("dsc")] public string? Body { get; set; }
    [JsonPropertyName("sy")] public List<string>? Symbols { get; set; }
    [JsonPropertyName("sc")] public List<string>? SectorCodes { get; set; }
}

/// <summary>Precomputed indicators from <c>/api/v3/indicators</c>, whole market in one call.</summary>
public sealed class AhlIndicatorsResponse
{
    [JsonPropertyName("data")] public List<AhlSymbolIndicators>? Data { get; set; }
}

public sealed class AhlSymbolIndicators
{
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    [JsonPropertyName("indicators")] public List<AhlIndicator>? Indicators { get; set; }
}

public sealed class AhlIndicator
{
    /// <summary>Family: <c>sma bb volt rsi stoch macd roc cci</c>.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("parameters")] public List<decimal>? Parameters { get; set; }
    /// <summary>Latest values. Multi-valued for some families: <c>bb</c> is [upper, mid, lower],
    /// <c>stoch</c> is [%K, %D], <c>macd</c> is [line, signal, histogram].</summary>
    [JsonPropertyName("values")] public List<decimal>? Values { get; set; }
}

/// <summary>PSX sector code → name. From the portal's own <c>sectors</c> table.</summary>
public static class AhlSectors
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["0801"] = "Automobile Assembler",
        ["0802"] = "Automobile Parts & Accessories",
        ["0803"] = "Cable & Electrical Goods",
        ["0804"] = "Cement",
        ["0805"] = "Chemical",
        ["0806"] = "Close-End Mutual Fund",
        ["0807"] = "Commercial Banks",
        ["0808"] = "Engineering",
        ["0809"] = "Fertilizer",
        ["0810"] = "Food & Personal Care Products",
        ["0811"] = "Glass & Ceramics",
        ["0812"] = "Insurance",
        ["0813"] = "Inv. Banks / Inv. Cos. / Securities Cos.",
        ["0814"] = "Jute",
        ["0815"] = "Leasing Companies",
        ["0816"] = "Leather & Tanneries",
        ["0818"] = "Miscellaneous",
        ["0819"] = "Modarabas",
        ["0820"] = "Oil & Gas Exploration Companies",
        ["0821"] = "Oil & Gas Marketing Companies",
        ["0822"] = "Paper & Board",
        ["0823"] = "Pharmaceuticals",
        ["0824"] = "Power Generation & Distribution",
        ["0825"] = "Refinery",
        ["0826"] = "Sugar & Allied Industries",
        ["0827"] = "Synthetic & Rayon",
        ["0828"] = "Technology & Communication",
        ["0829"] = "Textile Composite",
        ["0830"] = "Textile Spinning",
        ["0831"] = "Textile Weaving",
        ["0832"] = "Tobacco",
        ["0833"] = "Transport",
        ["0834"] = "Vanaspati & Allied Industries",
        ["0835"] = "Woollen",
        ["0836"] = "Real Estate Investment Trust",
        ["0837"] = "Exchange Traded Fund",
        ["0838"] = "Property"
    };

    public static string? Name(string? code) =>
        code is not null && Names.TryGetValue(code, out var n) ? n : null;
}
