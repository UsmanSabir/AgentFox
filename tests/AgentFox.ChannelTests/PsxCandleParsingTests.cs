using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// Verifies the PSX OHLC parsers against the portal's real markup. These two tables are the only
/// source of candles the trading agent has, so a silent parse regression would not fail loudly — it
/// would feed wrong prices into support/resistance levels and out the other end as a trade
/// recommendation. Hence: fixtures copied from live responses, plus explicit coverage of the failure
/// modes (missing prices, changed layout, unparseable input) where the parsers must yield NOTHING
/// rather than a plausible-looking zero.
/// </summary>
[TestClass]
public sealed class PsxCandleParsingTests
{
    // Real markup from POST dps.psx.com.pk/historical (date=2026-08-07), trimmed to three rows:
    // OGDC as published, a never-traded symbol with zero prices, and a row whose values are only in
    // the visible text (thousands separators included) rather than in data-value attributes.
    private const string HistoricalFragment = """
        <table class="tbl" id="historicalTable">
        <thead class="tbl__head"><tr>
          <th data-name="symbol">SYMBOL</th>
          <th class="right" data-name="ldcp">LDCP</th>
          <th class="right" data-name="open">OPEN</th>
          <th class="right" data-name="high">HIGH</th>
          <th class="right" data-name="low">LOW</th>
          <th class="right" data-name="close">CLOSE</th>
          <th class="right" data-name="change">CHANGE</th>
          <th class="right" data-name="percentChange">CHANGE (%)</th>
          <th class="right" data-name="volume">VOLUME</th>
        </tr></thead>
        <tbody class="tbl__body">
        <tr>
          <td data-value="OGDC"><strong>OGDC</strong></td>
          <td class="right" data-value="318.49">318.49</td>
          <td class="right" data-value="318.95">318.95</td>
          <td class="right" data-value="320">320.00</td>
          <td class="right" data-value="317">317.00</td>
          <td class="right" data-value="319.19">319.19</td>
          <td class="right change__text--pos" data-value="0.699"><i class="icon-up-dir"></i> 0.70</td>
          <td class="right change__text--pos" data-value="0.2197"><i class="icon-up-dir"></i> 0.22%</td>
          <td class="right" data-value="3666665">3,666,665</td>
        </tr>
        <tr>
          <td data-value="NOTRADE"><strong>NOTRADE</strong></td>
          <td class="right" data-value="12.5">12.50</td>
          <td class="right" data-value="0">0.00</td>
          <td class="right" data-value="0">0.00</td>
          <td class="right" data-value="0">0.00</td>
          <td class="right" data-value="0">0.00</td>
          <td class="right" data-value="0">0.00</td>
          <td class="right" data-value="0">0.00%</td>
          <td class="right" data-value="0">0</td>
        </tr>
        <tr>
          <td><strong>ABOT</strong></td>
          <td class="right">934.50</td>
          <td class="right">930.00</td>
          <td class="right">936.00</td>
          <td class="right">925.25</td>
          <td class="right">930.44</td>
          <td class="right"><i class="icon-down-dir"></i> -4.06</td>
          <td class="right"><i class="icon-down-dir"></i> -0.43%</td>
          <td class="right">25,578</td>
        </tr>
        </tbody></table>
        """;

    // Real markup from GET dps.psx.com.pk/market-watch. Note the column labelled CURRENT in the UI
    // carries data-name="close" — during a session it is the last trade, not a settled close.
    private const string MarketWatchFragment = """
        <table class="tbl" id="marketWatchTable">
        <thead class="tbl__head"><tr>
          <th data-name="symbol">SYMBOL</th>
          <th data-name="sector">SECTOR</th>
          <th data-name="listed">LISTED IN</th>
          <th class="right" data-name="ldcp">LDCP</th>
          <th class="right" data-name="open">OPEN</th>
          <th class="right" data-name="high">HIGH</th>
          <th class="right" data-name="low">LOW</th>
          <th class="right" data-name="close">CURRENT</th>
          <th class="right" data-name="change">CHANGE</th>
          <th class="right" data-name="percentChange">CHANGE (%)</th>
          <th class="right" data-name="volume">VOLUME</th>
        </tr></thead>
        <tbody class="tbl__body">
        <tr>
          <td data-search="OGDC" data-order="OGDC"><a class="tbl__symbol" href="/company/OGDC" data-title="Oil &amp; Gas Development Company Limited"><strong>OGDC</strong></a></td>
          <td>0820</td>
          <td>ALLSHR,KMI30,KSE100,KSE30</td>
          <td class="right" data-order="323.78">323.78</td>
          <td class="right" data-order="322.99">322.99</td>
          <td class="right" data-order="325.47">325.47</td>
          <td class="right" data-order="319.05">319.05</td>
          <td class="right" data-order="320.29">320.29</td>
          <td class="right change__text--neg" data-order="-3.49"><i class="icon-down-dir"></i> -3.49</td>
          <td class="right change__text--neg" data-order="-1.078"><i class="icon-down-dir"></i> -1.08%</td>
          <td class="right" data-order="3267148">3,267,148</td>
        </tr>
        </tbody></table>
        """;

    private static readonly DateOnly Session = new(2026, 8, 7);

    [TestMethod]
    public void ParseHistoricalTable_RealMarkup_ReadsOhlcAndVolume()
    {
        var rows = PsxDataClient.ParseHistoricalTable(HistoricalFragment, Session);

        Assert.IsTrue(rows.TryGetValue("OGDC", out var ogdc), "OGDC row should be parsed.");
        Assert.AreEqual(Session, ogdc!.Date);
        Assert.AreEqual(318.95m, ogdc.Open);
        Assert.AreEqual(320m, ogdc.High);
        Assert.AreEqual(317m, ogdc.Low);
        Assert.AreEqual(319.19m, ogdc.Close);
        Assert.AreEqual(318.49m, ogdc.PreviousClose);
        Assert.AreEqual(3_666_665L, ogdc.Volume);
        Assert.IsFalse(ogdc.IsLive);
    }

    [TestMethod]
    public void ParseHistoricalTable_VisibleTextOnly_StillParses()
    {
        // Rows without data-value attributes must fall back to the rendered text, commas and all.
        var rows = PsxDataClient.ParseHistoricalTable(HistoricalFragment, Session);

        Assert.IsTrue(rows.TryGetValue("ABOT", out var abot));
        Assert.AreEqual(930m, abot!.Open);
        Assert.AreEqual(936m, abot.High);
        Assert.AreEqual(925.25m, abot.Low);
        Assert.AreEqual(930.44m, abot.Close);
        Assert.AreEqual(25_578L, abot.Volume);
    }

    [TestMethod]
    public void ParseHistoricalTable_SymbolWithNoTrades_IsDropped()
    {
        // A zero-price row is not a candle. Keeping it would put a 0.00 "low" into the support
        // levels and make every stock look like it was one tick from free.
        var rows = PsxDataClient.ParseHistoricalTable(HistoricalFragment, Session);

        Assert.IsFalse(rows.ContainsKey("NOTRADE"));
        Assert.AreEqual(2, rows.Count);
    }

    [TestMethod]
    public void ParseHistoricalTable_ColumnsReorderedByPortal_StillMapsByName()
    {
        // Columns are read by their header data-name, so an inserted or moved column cannot shift
        // volume into the close field. Here VOLUME sits where CLOSE used to be.
        const string reordered = """
            <table><thead><tr>
              <th data-name="symbol">SYMBOL</th>
              <th data-name="volume">VOLUME</th>
              <th data-name="close">CLOSE</th>
              <th data-name="open">OPEN</th>
              <th data-name="high">HIGH</th>
              <th data-name="low">LOW</th>
            </tr></thead><tbody>
            <tr>
              <td data-value="LUCK">LUCK</td>
              <td data-value="1500">1,500</td>
              <td data-value="410.5">410.50</td>
              <td data-value="405">405.00</td>
              <td data-value="412">412.00</td>
              <td data-value="404">404.00</td>
            </tr>
            </tbody></table>
            """;

        var rows = PsxDataClient.ParseHistoricalTable(reordered, Session);

        Assert.IsTrue(rows.TryGetValue("LUCK", out var luck));
        Assert.AreEqual(410.5m, luck!.Close);
        Assert.AreEqual(1500L, luck.Volume);
    }

    [TestMethod]
    public void ParseHistoricalTable_NoRecognizableHeader_ReturnsEmpty()
    {
        // A portal layout change must surface as "no data" (which the caller reports as a warning),
        // never as rows parsed from guessed column positions.
        const string unknown = """
            <table><thead><tr><th>Ticker</th><th>Price</th></tr></thead>
            <tbody><tr><td>OGDC</td><td>319.19</td></tr></tbody></table>
            """;

        Assert.AreEqual(0, PsxDataClient.ParseHistoricalTable(unknown, Session).Count);
    }

    [TestMethod]
    public void ParseHistoricalTable_EmptyOrGarbage_ReturnsEmpty()
    {
        Assert.AreEqual(0, PsxDataClient.ParseHistoricalTable(null, Session).Count);
        Assert.AreEqual(0, PsxDataClient.ParseHistoricalTable("", Session).Count);
        Assert.AreEqual(0, PsxDataClient.ParseHistoricalTable("<html>maintenance</html>", Session).Count);
    }

    [TestMethod]
    public void ParseMarketWatchTable_ReadsLiveQuote()
    {
        var quotes = PsxDataClient.ParseMarketWatchTable(MarketWatchFragment, new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc));

        Assert.IsTrue(quotes.TryGetValue("OGDC", out var ogdc));
        Assert.AreEqual("Oil & Gas Development Company Limited", ogdc!.CompanyName);
        Assert.AreEqual("0820", ogdc!.Sector);
        Assert.AreEqual(323.78m, ogdc.PreviousClose);
        Assert.AreEqual(322.99m, ogdc.Open);
        Assert.AreEqual(325.47m, ogdc.High);
        Assert.AreEqual(319.05m, ogdc.Low);
        Assert.AreEqual(320.29m, ogdc.Current);
        Assert.AreEqual(-1.078m, ogdc.ChangePercent);
        Assert.AreEqual(3_267_148L, ogdc.Volume);
    }

    [TestMethod]
    public void LiveQuote_ToCandle_ProducesFormingBar()
    {
        var quotes = PsxDataClient.ParseMarketWatchTable(MarketWatchFragment, DateTime.UtcNow);
        var date = new DateOnly(2026, 8, 11);

        var candle = quotes["OGDC"].ToCandle(date);

        Assert.IsNotNull(candle);
        Assert.IsTrue(candle!.IsLive, "A market-watch bar is still forming and must be flagged as live.");
        Assert.AreEqual(date, candle.Date);
        Assert.AreEqual(320.29m, candle.Close);
        Assert.AreEqual(325.47m, candle.High);
        Assert.AreEqual(319.05m, candle.Low);
    }

    [TestMethod]
    public void LiveQuote_NotTradedToday_HasNoCandle()
    {
        var quote = new PsxLiveQuote { Symbol = "IDLE", PreviousClose = 50m, Current = 0m };

        Assert.IsNull(quote.ToCandle(new DateOnly(2026, 8, 11)));
    }

    [TestMethod]
    public void LiveQuote_ToCandle_KeepsLastPriceInsideTheRange()
    {
        // The portal occasionally publishes a last trade outside the reported high/low. The bar must
        // still be internally consistent, otherwise ATR and range maths go negative.
        var quote = new PsxLiveQuote
        {
            Symbol = "ODD", Open = 100m, High = 101m, Low = 99m, Current = 103m
        };

        var candle = quote.ToCandle(new DateOnly(2026, 8, 11))!;

        Assert.AreEqual(103m, candle.High);
        Assert.AreEqual(99m, candle.Low);
        Assert.IsTrue(candle.High >= candle.Close && candle.Close >= candle.Low);
    }
}
