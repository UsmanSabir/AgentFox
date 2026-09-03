<script lang="ts">
  import { onMount, onDestroy, tick, createEventDispatcher } from 'svelte';
  import {
    createChart,
    CandlestickSeries,
    HistogramSeries,
    LineSeries,
    createSeriesMarkers,
    type IChartApi,
    type ISeriesApi,
    type ISeriesMarkersPluginApi,
    type IPriceLine,
    type Time,
    type UTCTimestamp
  } from 'lightweight-charts';
  import {
    trading, CHART_INTERVALS,
    type ArmOrderDialogContext, type CandleArchiveStatus, type ChartData, type ChartInterval,
    type StockAssessment
  } from './api';
  import {
    LineChart, AlertTriangle, Eye, RefreshCw, Brain, Maximize2, Minimize2,
    Activity, Crosshair, BarChart3, TrendingDown, CalendarClock, Download
  } from 'lucide-svelte';
  import AssessmentCard from './AssessmentCard.svelte';
  import { livePriceLabel, useLivePrices, type LivePrice } from './livePrices';

  export let symbol: string | null = null;
  export let companyName: string | null = null;

  /** Raised when the user clicks a level to arm an order at it; the dashboard opens the dialog. */
  const dispatch = createEventDispatcher<{ arm: ArmOrderDialogContext }>();

  /** Full-width mode: the chart takes the row and the watchlist stacks beneath it. Bound by the parent. */
  export let expanded = false;
  /** Dockview owns panel sizing on desktop, so the legacy full-width toggle is hidden there. */
  export let allowExpand = true;

  /**
   * Incremented by the dashboard on its own timer. The parent owns the clock so there is ONE interval
   * for the page rather than one per component, and so the market-open flag driving it stays fresh.
   */
  export let refreshTick = 0;

  /** Archive progress from the dashboard's existing poll — no second status request from this pane. */
  export let archive: CandleArchiveStatus | null = null;

  /** Advances whenever that poll returns, allowing a chart to grow while a backfill is running. */
  export let historyRefreshTick = 0;

  /** Whether the market is currently open — a closed market has nothing new to fetch. */
  export let marketOpen = false;

  const livePrices = useLivePrices();
  let livePriceStore = livePrices.quote(symbol);
  $: livePriceStore = livePrices.quote(symbol);
  $: livePriceView = $livePriceStore;
  $: displayedPrice = livePriceView.quote?.current ?? data?.snapshot.close ?? null;
  $: displayedChange = livePriceView.quote?.changePercent ?? data?.snapshot.dayChangePercent ?? null;

  /**
   * How much level detail to draw. Six labelled price lines plus the axis chips is more than a small
   * pane can carry legibly, so this trades completeness for readability:
   *   all → the nearest three each side   key → weekly-confirmed only   off → clean price action
   * The full list is always in the legend below the chart, so nothing is actually hidden from view.
   */
  // Weekly-confirmed structure is the readable default. All nearby levels remain one click away and
  // are always listed below, but no longer cover the price axis on first render.
  let levelMode: 'all' | 'key' | 'off' = 'key';

  /** RSI in its own pane costs ~90px of candles; worth reclaiming when the pane is small. */
  let showRsi = true;
  let showVolume = true;

  const LEVEL_MODES = ['all', 'key', 'off'] as const;

  /** On-demand verdict for the charted symbol. Cleared when the symbol or interval changes. */
  let assessment: StockAssessment | null = null;
  let assessing = false;
  let assessmentError: string | null = null;
  let assessmentGeneration = 0;

  /**
   * Asks the parent to open the arming dialog, pre-filled from the clicked level.
   *
   * The default is the conventional level trade: buy a pullback at support, sell a rally at resistance.
   * These are entries, not protective-stop shortcuts; stop protection belongs to the plan action below.
   * Both direction and trigger remain editable in the dialog.
   */
  function armAtLevel(level: ChartData['levels']['supports'][number], side: 'support' | 'resistance') {
    if (!symbol || !data) return;
    const isSupport = side === 'support';
    const last = displayedPrice ?? data.snapshot.close;
    dispatch('arm', {
      symbol,
      triggerKind: isSupport ? 'PriceBelow' : 'PriceAbove',
      triggerPrice: level.price,
      action: isSupport ? 'BUY' : 'SELL',
      orderType: 'LIMIT',
      price: level.price,
      currentPrice: last,
      context:
        `${symbol} last ${last} · ${side} ${level.price} `
        + `(${level.touches} touch${level.touches === 1 ? '' : 'es'}`
        + `${level.weeklyConfirmed ? ', weekly-confirmed' : ', no weekly confirmation'}) `
        + `· ${isSupport ? 'a BUY fires if price falls to support' : 'a SELL fires if price rises to resistance'}.`
    });
  }

  /**
   * Arms a trailing SELL on a fall of `percent` from the current price.
   *
   * This is the one-click path for "get me out if it starts dropping", which needs no level reading at
   * all: the reference is the price on screen, the trigger trails it upward, and the order goes in at
   * market so a large fall still fills. Everything remains editable in the dialog — what these buttons
   * remove is the need to know which number goes in which box, not the chance to change it.
   */
  function armPercentDrop(percent: number) {
    if (!symbol || !data) return;
    const last = displayedPrice ?? data.snapshot.close;

    dispatch('arm', {
      symbol,
      triggerKind: 'PercentDrop',
      triggerPercent: percent,
      referencePrice: last,
      currentPrice: last,
      trailing: true,
      action: 'SELL',
      // Market, deliberately. A limit does NOT trail with the trigger, so on a trailing stop it is
      // the one combination that can trigger correctly and then fail to fill.
      orderType: 'MARKET',
      context:
        `${symbol} last ${last} · sells if it falls ${percent}% from its highest point after arming `
        + `(right now that is ${(Math.round(last * (1 - percent / 100) * 100) / 100)}). `
        + 'The trigger follows the price up and never back down.'
    });
  }

  /**
   * Arms the deterministic plan as one order: the BUY at its entry, with its stop attached.
   *
   * The plan is buy-side by construction (it is anchored on the nearest support, with the stop an ATR
   * multiple below it and the target at resistance), so the side is not a guess. The trigger is always
   * "price falls to", because an entry resting AT a support is reached from above — and when price has
   * already passed the level the plan sets entry to the last close, where a falls-to trigger fires on
   * the next evaluation rather than waiting for a dip that already happened.
   *
   * What this saves is not typing. Entry and stop are one decision — the level that would prove the
   * idea wrong is what makes the entry worth taking — and arming them from two separate clicks is how
   * an entry ends up in the market with the stop still in someone's head.
   */
  function armPlan() {
    if (!symbol || !data?.plan.entry) return;
    const plan = data.plan;
    const last = displayedPrice ?? data.snapshot.close;

    dispatch('arm', {
      symbol,
      triggerKind: 'PriceBelow',
      triggerPrice: plan.entry,
      action: 'BUY',
      orderType: 'LIMIT',
      price: plan.entry,
      // Only when the plan actually produced one. Forcing the checkbox on with no level would put the
      // dialog's generic 2%-under guess where a computed stop appears to be.
      attachStop: plan.stop != null,
      stopTrigger: plan.stop,
      currentPrice: last,
      context:
        `${symbol} last ${last} · plan entry ${plan.entry}, stop ${plan.stop ?? '—'}, `
        + `target ${plan.target ?? '—'}`
        + (plan.rewardRisk ? ` (R:R ${plan.rewardRisk})` : '')
        + `. ${plan.entryWeeklyConfirmed ? 'The entry level is weekly-confirmed.' : 'The entry level has no weekly confirmation.'}`
        + ' The plan is level arithmetic, not a recommendation — check the read below it before sizing.'
    });
  }

  async function assess() {
    if (!symbol || assessing) return;
    const requestedSymbol = symbol;
    const requestedInterval = interval;
    const generation = ++assessmentGeneration;
    assessing = true;
    assessmentError = null;
    try {
      const result = await trading.assess.symbol(requestedSymbol, requestedInterval);
      if (generation === assessmentGeneration
          && symbol === requestedSymbol && interval === requestedInterval) {
        assessment = result.assessment;
      }
    } catch (e) {
      if (generation === assessmentGeneration)
        assessmentError = e instanceof Error ? e.message : String(e);
    } finally {
      if (generation === assessmentGeneration) assessing = false;
    }
  }

  let interval: ChartInterval = '1D';
  let data: ChartData | null = null;
  let loading = false;
  let chartError: string | null = null;
  let loadGeneration = 0;
  let historyGap: CandleArchiveStatus['symbolsShortOfWeekly'][number] | null = null;
  let historyCheckedSessions = 0;
  let historyPercent = 0;

  $: historyGap = symbol
    ? archive?.symbolsShortOfWeekly.find(gap => gap.symbol === symbol) ?? null
    : null;
  $: historyCheckedSessions = historyGap && archive
    ? Math.max(0, archive.targetTradingDays - historyGap.missingSessions)
    : 0;
  $: historyPercent = archive?.targetTradingDays
    ? Math.min(100, Math.round(historyCheckedSessions / archive.targetTradingDays * 100))
    : 0;
  /**
   * A ticker that listed last week is not waiting for a download — the sessions it is short of have
   * not happened yet. It gets a plain statement of that instead of a progress bar, which in this
   * state would tick along toward a completeness the symbol can never reach and read as stuck.
   */
  $: newListing = historyGap?.noEarlierHistory ?? false;
  $: sessionsUntilWeekly = historyGap && archive?.dailyBarsForWeekly
    ? Math.max(0, archive.dailyBarsForWeekly - historyGap.archivedBars)
    : 0;

  /** Starts the targeted backfill from here, so a chart sitting on a stalled history is not a dead end. */
  let fetchingHistory = false;
  let fetchNotice: string | null = null;
  async function fetchHistory() {
    if (!symbol || fetchingHistory) return;
    fetchingHistory = true;
    fetchNotice = null;
    try {
      const result = await trading.startBackfill(undefined, [symbol]);
      fetchNotice = result.started
        ? 'Fetching the sessions this symbol was never requested for. The chart grows as they arrive.'
        : 'A backfill pass is already running; this symbol is fetched once it finishes.';
    } catch (e) {
      fetchNotice = e instanceof Error ? e.message : String(e);
    } finally {
      fetchingHistory = false;
    }
  }

  let container: HTMLDivElement | null = null;
  let chart: IChartApi | null = null;
  let candleSeries: ISeriesApi<'Candlestick'> | null = null;
  let volumeSeries: ISeriesApi<'Histogram'> | null = null;
  let sma20Series: ISeriesApi<'Line'> | null = null;
  let sma50Series: ISeriesApi<'Line'> | null = null;
  let rsiSeries: ISeriesApi<'Line'> | null = null;
  let priceLines: IPriceLine[] = [];
  // Overlay artefacts are owned separately from the core ones so a render can replace them without
  // disturbing the support/resistance lines or the plan markers.
  let overlaySeries: ISeriesApi<'Line'>[] = [];
  let overlayPriceLines: IPriceLine[] = [];
  let overlayMarkers: ISeriesMarkersPluginApi<Time> | null = null;
  let markers: ISeriesMarkersPluginApi<Time> | null = null;
  let resizeObserver: ResizeObserver | null = null;

  // Set from the response before the chart is built, so the RSI bands drawn are the ones the backend
  // actually classified against. The defaults match TradingScanOptions.
  let rsiOversold = 35;
  let rsiOverbought = 70;

  const isIntraday = (value: ChartInterval | string) => value.endsWith('m');

  // Read the host theme's tokens rather than hardcoding colors, so the chart follows the app in both
  // light and dark. Falls back to the dark palette if a token is missing.
  function token(name: string, fallback: string): string {
    if (typeof getComputedStyle === 'undefined') return fallback;
    const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return value || fallback;
  }

  function buildChart(el: HTMLDivElement) {
    const text = token('--text-3', '#5f6377');
    const grid = token('--border', 'rgba(255,255,255,0.06)');

    chart = createChart(el, {
      layout: {
        background: { color: 'transparent' },
        textColor: text,
        fontSize: 11,
        attributionLogo: false,
        panes: { separatorColor: grid }
      },
      grid: { vertLines: { color: grid }, horzLines: { color: grid } },
      rightPriceScale: { borderColor: grid },
      // rightOffset keeps a few bars of empty space so the entry/stop/target markers on the LAST bar
      // are not clipped by the price axis.
      timeScale: {
        borderColor: grid, timeVisible: isIntraday(interval), secondsVisible: false, rightOffset: 6
      },
      crosshair: { mode: 0 },
      autoSize: false,
      height: el.clientHeight,
      width: el.clientWidth
    });

    candleSeries = chart.addSeries(CandlestickSeries, {
      upColor: token('--success', '#34d399'),
      downColor: token('--danger', '#f87171'),
      borderUpColor: token('--success', '#34d399'),
      borderDownColor: token('--danger', '#f87171'),
      wickUpColor: token('--success', '#34d399'),
      wickDownColor: token('--danger', '#f87171')
    });

    // Volume shares the price pane as an overlay pinned to the bottom 20% — a separate pane would
    // steal height from the candles, which are what the decision is actually made on.
    volumeSeries = chart.addSeries(HistogramSeries, {
      priceScaleId: 'volume',
      priceFormat: { type: 'volume' },
      color: token('--text-3', '#5f6377'),
      visible: showVolume
    });
    chart.priceScale('volume').applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });

    sma20Series = chart.addSeries(LineSeries, {
      color: token('--primary', '#818cf8'), lineWidth: 1,
      priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false
    });
    sma50Series = chart.addSeries(LineSeries, {
      color: token('--accent', '#a78bfa'), lineWidth: 1, lineStyle: 2,
      priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false
    });

    // RSI gets its own pane: plotted on the price scale it would be an invisible flat line. Omitted
    // entirely when hidden, so the candles get the height back rather than a collapsed empty pane.
    if (showRsi) {
      rsiSeries = chart.addSeries(LineSeries, {
        color: token('--info', '#60a5fa'), lineWidth: 1, priceLineVisible: false
      }, 1);
      chart.panes()[1]?.setHeight(expanded ? 120 : 90);

      // The oversold/overbought bands the analyzer actually classifies against (defaults 35/70), so a
      // reader can see why a snapshot called RSI extreme rather than guessing at the usual 30/70.
      for (const band of [{ price: rsiOverbought, label: 'overbought' }, { price: rsiOversold, label: 'oversold' }]) {
        rsiSeries.createPriceLine({
          price: band.price,
          color: token('--text-3', '#5f6377'),
          lineWidth: 1,
          lineStyle: 3,
          axisLabelVisible: true,
          title: band.label
        });
      }
    }

    resizeObserver = new ResizeObserver(() => {
      if (chart && el.clientWidth > 0) {
        chart.applyOptions({ width: el.clientWidth, height: el.clientHeight });
      }
    });
    resizeObserver.observe(el);
  }

  function render(d: ChartData) {
    if (!container) return;
    if (!chart) buildChart(container);
    if (!chart || !candleSeries || !volumeSeries || !sma20Series || !sma50Series) return;

    chart.applyOptions({ timeScale: { timeVisible: isIntraday(d.interval), secondsVisible: false } });

    const up = token('--success', '#34d399');
    const down = token('--danger', '#f87171');

    candleSeries.setData(d.candles.map(c => ({
      time: c.time as UTCTimestamp,
      open: c.open, high: c.high, low: c.low, close: c.close
    })));

    volumeSeries.setData(d.candles.map(c => ({
      time: c.time as UTCTimestamp,
      value: c.volume,
      // Tinted by the bar's own direction, so a volume spike reads as buying or selling at a glance.
      color: `${c.close >= c.open ? up : down}55`
    })));

    // Nulls are dropped rather than zeroed: a zero would draw the SMA down to the axis for the first
    // 20 bars and make it look like a crash.
    sma20Series.setData(d.candles.filter(c => c.sma20 != null)
      .map(c => ({ time: c.time as UTCTimestamp, value: c.sma20! })));
    sma50Series.setData(d.candles.filter(c => c.sma50 != null)
      .map(c => ({ time: c.time as UTCTimestamp, value: c.sma50! })));
    rsiSeries?.setData(d.candles.filter(c => c.rsi14 != null)
      .map(c => ({ time: c.time as UTCTimestamp, value: c.rsi14! })));

    drawLevels(d);
    drawPlanMarkers(d);
    const projectedBars = drawOverlays(d);
    // Logical range ignores weekends and archive gaps, so the latest candles use the available width
    // instead of being compressed into the right half of the plot by calendar time.
    if (d.candles.length > 0) {
      const visibleBars = expanded ? 110 : 72;
      chart.timeScale().setVisibleLogicalRange({
        from: Math.max(-1, d.candles.length - visibleBars),
        // Room for whatever projects past the last bar. Without this the projected tail renders
        // off-screen and the feature reads as broken rather than as absent.
        to: d.candles.length + Math.max(4, projectedBars + 2)
      });
    }
    applyLiveQuote(livePriceView.quote);
  }

  /**
   * AHL publishes session OHLC, so it can safely replace only the forming DAILY bar. Intraday candle
   * shape remains server-authored; using a session high as a five-minute high would fabricate data.
   */
  function applyLiveQuote(quote: LivePrice | null) {
    if (!quote || !data || !data.usesLiveBar || !candleSeries
        || data.interval !== '1D' || data.candles.length === 0) return;
    if (quote.symbol !== data.symbol.trim().toUpperCase() || quote.current == null) return;
    const last = data.candles[data.candles.length - 1];
    const open = quote.open ?? last.open;
    const high = Math.max(quote.high ?? last.high, quote.current, open);
    const low = Math.min(quote.low ?? last.low, quote.current, open);
    candleSeries.update({
      time: last.time as UTCTimestamp,
      open,
      high,
      low,
      close: quote.current
    });
    if (quote.volume != null) {
      const color = quote.current >= open ? token('--success', '#34d399') : token('--danger', '#f87171');
      volumeSeries?.update({ time: last.time as UTCTimestamp, value: quote.volume, color: `${color}55` });
    }
  }

  $: applyLiveQuote(livePriceView.quote);

  /**
   * Drops the overlay handles without touching the chart. Called wherever the chart itself is
   * destroyed, so the next render starts from a clean slate rather than holding references into a
   * disposed chart.
   */
  function forgetOverlayArtifacts() {
    overlaySeries = [];
    overlayPriceLines = [];
    overlayMarkers = null;
  }

  /**
   * Maps an overlay's semantic `kind` to a theme token. Overlays never carry colors: an edition that
   * sent its own would own this dashboard's palette and break one of the two themes. An unrecognised
   * kind lands on neutral rather than throwing, so a newer backend cannot break an older client.
   */
  function overlayColor(kind: string): string {
    switch (kind) {
      case 'projection':
      case 'prediction': return token('--accent', '#a78bfa');
      case 'target':     return token('--success', '#34d399');
      case 'stop':       return token('--danger', '#f87171');
      case 'entry':      return token('--primary', '#818cf8');
      case 'support':    return token('--success', '#34d399');
      case 'resistance': return token('--danger', '#f87171');
      default:           return token('--info', '#60a5fa');
    }
  }

  /**
   * Draws whatever the backend asked for: edition overlays — projections, predicted points, a next
   * target, a confidence band — on this same chart rather than on a second page.
   *
   * Empty in the community build, so this is a no-op there. Returns how many bars the overlays
   * project PAST the last candle, which the caller needs to leave room for.
   *
   * Everything here is presentation. Nothing drawn from an overlay is ever an execution input; the
   * order path reads the server-side model, never this response.
   */
  function drawOverlays(d: ChartData): number {
    // Tear down last render's artefacts first — overlays change per symbol and per interval, and a
    // stale projection left on the chart would be read as a current one.
    //
    // Guarded: if the chart was destroyed and rebuilt between renders these handles belong to a
    // chart that no longer exists, and lightweight-charts throws on them. An exception here would
    // abort the whole draw and take the overlay layer down silently, so a stale handle is simply
    // dropped instead. forgetOverlayArtifacts() at each teardown site is the primary fix; this is
    // the backstop for a teardown site added later that forgets to call it.
    try {
      for (const s of overlaySeries) chart!.removeSeries(s);
      for (const line of overlayPriceLines) candleSeries!.removePriceLine(line);
      overlayMarkers?.setMarkers([]);
    } catch {
      // handles from a disposed chart; nothing to clean up
    }
    forgetOverlayArtifacts();

    const o = d.overlays;
    if (!o) return 0;

    const lastBar = d.candles.length ? d.candles[d.candles.length - 1].time : 0;
    let furthest = lastBar;

    for (const level of o.levels ?? []) {
      overlayPriceLines.push(candleSeries!.createPriceLine({
        price: level.price,
        color: overlayColor(level.kind),
        lineWidth: Math.min(3, Math.max(1, level.weight)) as 1 | 2 | 3,
        lineStyle: level.confirmed ? 0 : 2,
        axisLabelVisible: true,
        title: level.label
      }));
    }

    const addLine = (
      id: string, kind: string, dashed: boolean,
      points: { time: number; value: number }[]
    ) => {
      if (points.length === 0) return;
      const series = chart!.addSeries(LineSeries, {
        color: overlayColor(kind),
        lineWidth: 2,
        lineStyle: dashed ? 2 : 0,
        priceLineVisible: false,
        lastValueVisible: false,
        crosshairMarkerVisible: false
      });
      // Sorted and de-duplicated by time: lightweight-charts requires strictly ascending times and
      // throws on an out-of-order point, which would take down the whole chart for one bad overlay.
      const clean = [...points]
        .sort((a, b) => a.time - b.time)
        .filter((pt, i, all) => i === 0 || pt.time !== all[i - 1].time);
      series.setData(clean.map(pt => ({ time: pt.time as UTCTimestamp, value: pt.value })));
      overlaySeries.push(series);
      const last = clean[clean.length - 1].time;
      if (last > furthest) furthest = last;
    };

    for (const series of o.series ?? [])
      addLine(series.id, series.kind, series.dashed, series.points ?? []);

    // A band is two lines. Drawn as an upper and a lower edge rather than a filled area because
    // lightweight-charts has no band primitive, and two dashed edges read correctly either way.
    for (const band of o.bands ?? []) {
      const pts = band.points ?? [];
      addLine(`${band.id}:upper`, band.kind, true, pts.map(pt => ({ time: pt.time, value: pt.upper })));
      addLine(`${band.id}:lower`, band.kind, true, pts.map(pt => ({ time: pt.time, value: pt.lower })));
    }

    const markerList = (o.markers ?? []).map(m => ({
      time: m.time as UTCTimestamp,
      position: (m.position === 'belowBar' ? 'belowBar' : 'aboveBar') as 'aboveBar' | 'belowBar',
      color: overlayColor(m.kind),
      shape: 'circle' as const,
      text: m.text
    })).sort((a, b) => (a.time as number) - (b.time as number));
    if (markerList.length > 0) {
      for (const m of markerList) if ((m.time as number) > furthest) furthest = m.time as number;
      if (overlayMarkers) overlayMarkers.setMarkers(markerList);
      else overlayMarkers = createSeriesMarkers(candleSeries!, markerList);
    }

    if (furthest <= lastBar || d.candles.length < 2) return 0;
    // Convert the projected span into BARS, using this series' own median bar spacing. Logical range
    // counts bars, not seconds, so a seconds-based figure would be meaningless to the time scale.
    const spacing = medianBarSpacing(d);
    return spacing > 0 ? Math.ceil((furthest - lastBar) / spacing) : 0;
  }

  /** Median gap between bars — robust to weekends and archive gaps in a way a mean is not. */
  function medianBarSpacing(d: ChartData): number {
    const gaps: number[] = [];
    for (let i = 1; i < d.candles.length; i++) {
      const gap = d.candles[i].time - d.candles[i - 1].time;
      if (gap > 0) gaps.push(gap);
    }
    if (gaps.length === 0) return 0;
    gaps.sort((a, b) => a - b);
    return gaps[Math.floor(gaps.length / 2)];
  }

  /**
   * Support and resistance as horizontal price lines. Line width encodes touch count and a solid line
   * means the weekly chart confirms the level — the two things that separate a level worth trading
   * from a line through one bar.
   */
  function drawLevels(d: ChartData) {
    if (!candleSeries) return;
    for (const line of priceLines) candleSeries.removePriceLine(line);
    priceLines = [];
    if (levelMode === 'off') return;

    const add = (level: ChartData['levels']['supports'][number], color: string, side: string) => {
      priceLines.push(candleSeries!.createPriceLine({
        price: level.price,
        color,
        lineWidth: Math.min(3, Math.max(1, level.touches)) as 1 | 2 | 3,
        lineStyle: level.weeklyConfirmed ? 0 : 2,
        axisLabelVisible: true,
        title: `${side} ${level.price}${level.weeklyConfirmed ? ' ✓W' : ''} ×${level.touches}`
      }));
    };

    // Only the nearest few each way: every cluster drawn at once is an unreadable grid. In 'key' mode
    // just the weekly-confirmed ones — the levels that are structure rather than a recent swing.
    for (const s of pickLevels(d.levels.supports)) add(s, token('--success', '#34d399'), 'S');
    for (const r of pickLevels(d.levels.resistances)) add(r, token('--danger', '#f87171'), 'R');
  }

  function pickLevels(levels: ChartData['levels']['supports']) {
    const nearest = levels.slice(0, 3);
    return levelMode === 'key' ? nearest.filter(l => l.weeklyConfirmed) : nearest;
  }

  /**
   * Tears the chart down and builds it again. Used for layout changes (pane count, size) rather than
   * mutating the existing chart: lightweight-charts has no clean way to add or drop a pane after the
   * fact, and a rebuild over a few hundred bars is imperceptible.
   */
  async function rebuild() {
    await tick(); // let the container take its new size first, so the canvas is measured correctly
    resizeObserver?.disconnect();
    resizeObserver = null;
    chart?.remove();
    chart = null;
    candleSeries = volumeSeries = sma20Series = sma50Series = rsiSeries = null;
    priceLines = [];
    markers = null;
    forgetOverlayArtifacts();
    if (data) render(data);
  }

  function cycleLevels() {
    levelMode = LEVEL_MODES[(LEVEL_MODES.indexOf(levelMode) + 1) % LEVEL_MODES.length];
    // Levels are price lines on an existing series, so this needs no rebuild.
    if (data) drawLevels(data);
  }

  function toggleRsi() {
    showRsi = !showRsi;
    rebuild();
  }

  function toggleVolume() {
    showVolume = !showVolume;
    volumeSeries?.applyOptions({ visible: showVolume });
  }

  function toggleExpand() {
    expanded = !expanded;
    rebuild();
  }

  /** Entry / stop / target from the deterministic plan, anchored on the last bar. */
  function drawPlanMarkers(d: ChartData) {
    if (!candleSeries || !d.candles.length) return;
    const time = d.candles[d.candles.length - 1].time as UTCTimestamp;
    const points = [
      { value: d.plan.entry, text: 'Entry', color: token('--primary', '#818cf8'), position: 'belowBar' },
      { value: d.plan.stop, text: 'Stop', color: token('--danger', '#f87171'), position: 'belowBar' },
      { value: d.plan.target, text: 'Target', color: token('--success', '#34d399'), position: 'aboveBar' }
    ].filter(p => p.value != null);

    const list = points.map(p => ({
      time,
      position: p.position as 'aboveBar' | 'belowBar',
      color: p.color,
      shape: 'circle' as const,
      text: `${p.text} ${p.value}`
    }));

    if (markers) markers.setMarkers(list);
    else markers = createSeriesMarkers(candleSeries, list);
  }

  /**
   * @param keepAssessment true for an auto-refresh of the SAME symbol and interval — the verdict is
   * still about this chart, and discarding it every minute would make the button useless.
   */
  async function load(keepAssessment = false) {
    if (!symbol) { data = null; return; }
    const requestedSymbol = symbol;
    const requestedInterval = interval;
    const sameChart = data?.symbol === requestedSymbol && data.interval === requestedInterval;
    const generation = ++loadGeneration;
    lastHistoryReloadAt = Date.now();
    loading = true;
    chartError = null;
    // A verdict belongs to the symbol and interval it was produced for; carrying it across a change
    // would attach one stock's judgement to another's chart.
    if (!keepAssessment) {
      assessmentGeneration++;
      assessment = null;
      assessmentError = null;
      assessing = false;
    }
    // A stale refresh is useful for the same chart, but showing the previous ticker or timeframe
    // under a newly selected header is dangerous. Tear down only when the chart's identity changed.
    if (!sameChart) {
      resizeObserver?.disconnect();
      resizeObserver = null;
      chart?.remove();
      chart = null;
      candleSeries = volumeSeries = sma20Series = sma50Series = rsiSeries = null;
      priceLines = [];
      markers = null;
      forgetOverlayArtifacts();
      data = null;
    }
    try {
      const next = await trading.candles(requestedSymbol, requestedInterval);
      if (generation !== loadGeneration
          || symbol !== requestedSymbol || interval !== requestedInterval) return;
      data = next;
      rsiOversold = next.thresholds.rsiOversold;
      rsiOverbought = next.thresholds.rsiOverbought;
      render(next);
    } catch (e) {
      if (generation === loadGeneration)
        chartError = e instanceof Error ? e.message : String(e);
    } finally {
      if (generation === loadGeneration) loading = false;
    }
  }

  function pick(next: ChartInterval) {
    if (next === interval) return;
    interval = next;
    load();
  }

  // Reload whenever the watchlist selection changes. Guarded on `symbol` so the initial null does not
  // fire a request.
  let lastLoaded: string | null = null;
  $: if (symbol && symbol !== lastLoaded) {
    lastLoaded = symbol;
    // The backfill notice is about the symbol it was raised for; carrying it to the next one would
    // report a fetch that was never started for it.
    fetchNotice = null;
    load();
  }

  /**
   * Follow the market while it is open.
   *
   * Skipped when the tab is hidden (a background tab does not need a live chart, and the request still
   * costs a portal round-trip), while a load is already in flight, and while the market is closed —
   * settled candles do not change, so polling them is pure waste.
   */
  let lastTick = 0;
  $: if (refreshTick !== lastTick) {
    lastTick = refreshTick;
    if (symbol && marketOpen && !loading && typeof document !== 'undefined' && !document.hidden) {
      load(true);
    }
  }

  /**
   * Grow a newly-added symbol in place as its archive fills. The dashboard already polls progress
   * every four seconds; this pane deliberately refreshes at most every 12 seconds so a historical
   * download cannot turn into a chart-request storm. One final refresh runs when the pass ends.
   */
  const HISTORY_REFRESH_MS = 12_000;
  let lastHistoryTick = historyRefreshTick;
  let lastHistoryReloadAt = 0;
  let historyWasRunning = false;
  $: if (historyRefreshTick !== lastHistoryTick) {
    lastHistoryTick = historyRefreshTick;
    const runningForSymbol = Boolean(symbol && historyGap && archive?.progress.isRunning);
    const justFinished = historyWasRunning && !runningForSymbol;
    historyWasRunning = runningForSymbol;
    const visible = typeof document === 'undefined' || !document.hidden;
    const refreshDue = Date.now() - lastHistoryReloadAt >= HISTORY_REFRESH_MS;
    if (symbol && !loading && visible && (justFinished || (runningForSymbol && refreshDue))) {
      load(true);
    }
  }

  // The container only exists once a symbol is selected, so rendering waits for the element rather
  // than the fetch.
  $: if (container && data && !chart) render(data);

  onMount(() => {
    // Most of the plugin follows CSS variables automatically. The canvas resolves those variables to
    // concrete colors when it is built, so rebuild it when the host changes theme.
    const handleThemeChange = () => rebuild();
    window.addEventListener('agentfox:themechange', handleThemeChange);
    return () => window.removeEventListener('agentfox:themechange', handleThemeChange);
  });

  onDestroy(() => {
    resizeObserver?.disconnect();
    chart?.remove();
    chart = null;
  });

  const pct = (value: number | null | undefined) =>
    value == null ? '—' : `${value > 0 ? '+' : ''}${value}%`;
</script>

<section class="chart-card">
  <header>
    <div class="title">
      <LineChart size={16} />
      <div>
        <div class="instrument">
          <b>{symbol ?? 'Chart'}</b>
          {#if companyName}<strong>{companyName}</strong>{/if}
        </div>
        {#if data}
          <div class="quote-line">
            <span class="price-readout">
              <small>{livePriceView.quote ? livePriceLabel(livePriceView) : data.usesLiveBar ? 'Current price' : 'Last close'}</small>
              <strong>{displayedPrice ?? '—'}</strong>
            </span>
            <span class:positive={(displayedChange ?? 0) > 0}
              class:negative={(displayedChange ?? 0) < 0}>
              {pct(displayedChange)}
            </span>
            <span>{data.snapshot.setup} · {data.barsAnalyzed} bars · as of {data.snapshot.asOf}</span>
            {#if data.usesLiveBar}
              <em title="The exchange is open and this candle is still forming">live candle</em>
            {/if}
          </div>
        {:else}
          <span>Candles, support/resistance, and indicators</span>
        {/if}
      </div>
    </div>

    <div class="head-actions">
      {#if data && !data.tradable}
        <span class="chip monitor" title="Not in AllowedSymbols — an order would be rejected by the risk engine">
          <Eye size={11} /> monitor-only
        </span>
      {/if}
      <div class="intervals">
        {#each CHART_INTERVALS as option}
          <button class:active={interval === option} on:click={() => pick(option)} disabled={loading || !symbol}>
            {option}
          </button>
        {/each}
      </div>

      <!-- Declutter controls. The full level list stays in the legend below, so turning lines off
           hides drawing, never information. -->
      <div class="view-controls">
        <button
          class:muted={levelMode === 'off'}
          on:click={cycleLevels}
          disabled={!symbol}
          title={levelMode === 'all' ? 'Showing the nearest 3 levels each side — click for weekly-confirmed only'
               : levelMode === 'key' ? 'Showing weekly-confirmed levels only — click to hide all lines'
               : 'Level lines hidden — click to show all'}
        >Levels: {levelMode}</button>
        <button class:muted={!showRsi} on:click={toggleRsi} disabled={!symbol}
          title={showRsi ? 'Hide the RSI pane and give the height back to the candles' : 'Show the RSI pane'}>
          <Activity size={11} /> RSI
        </button>
        <button class:muted={!showVolume} on:click={toggleVolume} disabled={!symbol}
          title={showVolume ? 'Hide volume bars for a cleaner price chart' : 'Show volume bars'}>
          <BarChart3 size={11} /> Volume
        </button>
      </div>

      <button
        class="btn btn-ghost assess-btn"
        title="Ask the model how much confidence the evidence supports (one model call)"
        on:click={assess}
        disabled={assessing || loading || !symbol}
      ><Brain size={13} /> {assessing ? 'Assessing…' : 'Assess'}</button>
      <button class="icon" title="Refresh" on:click={() => load()} disabled={loading || !symbol}>
        <RefreshCw size={13} />
      </button>
      {#if allowExpand}
        <button
          class="icon"
          title={expanded ? 'Collapse — put the watchlist back beside the chart' : 'Expand — full width, taller, watchlist moves below'}
          on:click={toggleExpand}
          disabled={!symbol}
        >
          {#if expanded}<Minimize2 size={13} />{:else}<Maximize2 size={13} />{/if}
        </button>
      {/if}
    </div>
  </header>

  {#if symbol && historyGap}
    <div class="history-status" class:new-listing={newListing} role="status" aria-live="polite">
      <div class="history-copy">
        {#if newListing}
          <CalendarClock size={14} aria-hidden="true" />
        {:else}
          <RefreshCw size={14} class={archive?.progress.isRunning ? 'spinning' : ''} aria-hidden="true" />
        {/if}
        <div>
          <b>
            {#if newListing}
              {symbol} has only {historyGap.archivedBars} session{historyGap.archivedBars === 1 ? '' : 's'} of history
            {:else if archive?.progress.isRunning}
              Building {symbol} history
            {:else}
              {symbol} history is still limited
            {/if}
          </b>
          <span>
            {#if newListing}
              {#if historyGap.firstBarDate}First traded {historyGap.firstBarDate} · {/if}
              {historyGap.sessionsWithoutTrade} earlier session{historyGap.sessionsWithoutTrade === 1 ? '' : 's'}
              checked, no trading
            {:else}
              {historyGap.archivedBars} daily bar{historyGap.archivedBars === 1 ? '' : 's'} available
              {#if archive?.targetTradingDays}
                · {historyCheckedSessions} of {archive.targetTradingDays} sessions checked
              {/if}
            {/if}
          </span>
        </div>
      </div>
      <!-- No progress bar for a new listing: there is nothing left to download, so a bar could only
           promise history that does not exist. -->
      {#if !newListing && archive?.targetTradingDays}
        <div class="history-progress">
          <progress max={archive.targetTradingDays} value={historyCheckedSessions}
            aria-label={`${symbol} history backfill progress`}></progress>
          <strong>{historyPercent}%</strong>
          {#if !archive.progress.isRunning}
            <!-- The bar stops moving when no pass is running, which is indistinguishable from stuck.
                 Offer the thing that would move it rather than leaving it to be watched. -->
            <button class="fetch" type="button" on:click={fetchHistory} disabled={fetchingHistory}>
              <Download size={12} aria-hidden="true" /> Fetch history
            </button>
          {/if}
        </div>
      {/if}
      <small>
        {#if newListing}
          There is no earlier history to download — this ticker had not started trading. Weekly levels
          need {archive?.dailyBarsForWeekly ?? 0} sessions and become available after about
          {sessionsUntilWeekly} more trading day{sessionsUntilWeekly === 1 ? '' : 's'}. Daily levels
          and the plan below are drawn from what it has.
        {:else if fetchNotice}
          {fetchNotice}
        {:else if archive?.progress.isRunning}
          Showing available candles now; this chart updates automatically as more history arrives.
        {:else}
          The chart is usable, but weekly levels remain provisional until more history is archived.
        {/if}
      </small>
    </div>
  {/if}

  {#if !symbol}
    <p class="msg">Select a symbol from the watchlist.</p>
  {:else if loading && !data}
    <div class="plot loading-state" role="status" aria-live="polite">
      <RefreshCw size={18} class="spinning" aria-hidden="true" />
      <span>Loading available candles…</span>
    </div>
  {:else if chartError && !data}
    <p class:danger={!historyGap} class="msg">
      {#if newListing}
        This ticker has no settled session on record yet. Its first candle appears after its first
        full trading day.
      {:else if historyGap}
        The first chart point is not available yet. History is continuing in the background.
      {:else}
        {chartError}
      {/if}
      <button class="retry" on:click={() => load()}>Retry</button>
    </p>
  {:else}
    <div class="plot" class:loading class:tall={expanded} bind:this={container}></div>

    {#if data}
      <div class="readout">
        {#if chartError}
          <p class="inline-error">Chart refresh failed; showing the last successful data. {chartError}
            <button class="retry" on:click={() => load(true)}>Retry</button>
          </p>
        {/if}
        {#if assessmentError}
          <p class="inline-error">Assessment failed: {assessmentError}
            <button class="retry" on:click={assess}>Retry</button>
          </p>
        {/if}
        {#if assessment}
          <AssessmentCard {assessment} />
        {/if}

        <div class="metrics">
          <div><span>Zone</span><b>{data.snapshot.zone}</b></div>
          <div><span>RSI(14)</span><b>{data.snapshot.rsi14 ?? '—'}</b></div>
          <div><span>ATR(14)</span><b>{data.snapshot.atr14 ?? '—'}{#if data.snapshot.atrPercent} ({data.snapshot.atrPercent}%){/if}</b></div>
          <div><span>Trend</span><b>{data.snapshot.trend ?? '—'}</b></div>
          <div><span>Volume</span><b>{data.snapshot.volume.toLocaleString()}{#if data.snapshot.volumeRatio} · {data.snapshot.volumeRatio}×{/if}</b></div>
          <div>
            <span>Weekly</span>
            <b class:danger={data.weekly.breakdown}>
              {data.weekly.breakdown ? 'breakdown' : data.weekly.alignment}
            </b>
          </div>
        </div>

        {#if data.plan.entry != null}
          <div class="plan">
            <b>Plan</b>
            <span>entry {data.plan.entry}</span>
            <span>stop {data.plan.stop ?? '—'}</span>
            <span>target {data.plan.target ?? '—'}</span>
            {#if data.plan.rewardRisk}<span class="rr">R:R {data.plan.rewardRisk}</span>{/if}
            <!-- plan.entryWeeklyConfirmed, not weekly.entryLevelConfirmed: the latter describes the
                 full-history nearest support, which may not be the level shown here. -->
            {#if data.plan.entryWeeklyConfirmed}
              <span class="chip ok">weekly-confirmed level</span>
            {:else}
              <span class="chip warn">no weekly confirmation</span>
            {/if}
            <button
              class="arm-plan"
              on:click={armPlan}
              title="Arm a BUY at {data.plan.entry}{data.plan.stop != null
                ? `, protected by a stop at ${data.plan.stop}`
                : ''}"
            >
              <Crosshair size={12} />
              arm{data.plan.stop != null ? ' with stop' : ''}
            </button>
          </div>
        {/if}

        <!-- The no-chart-reading path. A support/resistance click needs you to have decided which
             level matters; this only needs the size of fall you are unwilling to sit through, which
             is a question anyone holding the stock can already answer. -->
        <div class="drop-guard">
          <b><TrendingDown size={12} /> Sell if it drops</b>
          {#each [2, 3, 5, 10] as percent}
            <button
              class="drop-btn"
              on:click={() => armPercentDrop(percent)}
              title="Arm a trailing SELL that fires if {symbol} falls {percent}% from its highest point — at market, so it fills"
            >
              −{percent}%
              <em>{(Math.round((displayedPrice ?? data.snapshot.close) * (1 - percent / 100) * 100) / 100)}</em>
            </button>
          {/each}
          <span class="drop-hint">trailing · follows the price up</span>
        </div>

        <!-- Defaults follow the ordinary level trade: sell at resistance, buy at support. -->
        <div class="levels">
          <div>
            <b>Resistance <em class="hint">click to arm</em></b>
            {#each data.levels.resistances.slice(0, 3) as level}
              <button
                class="level armable"
                title="Arm a SELL when price rises to resistance at {level.price}"
                on:click={() => armAtLevel(level, 'resistance')}
              >
                <span class="action sell">SELL</span> {level.price}
                <em>{pct(level.distancePercent)} · ×{level.touches}{level.weeklyConfirmed ? ' · weekly ✓' : ''}</em>
              </button>
            {:else}<span class="level muted">none above price</span>{/each}
          </div>
          <div>
            <b>Support <em class="hint">click to arm</em></b>
            {#each data.levels.supports.slice(0, 3) as level}
              <button
                class="level armable"
                title="Arm a BUY when price falls to support at {level.price}"
                on:click={() => armAtLevel(level, 'support')}
              >
                <span class="action buy">BUY</span> {level.price}
                <em>−{level.distancePercent ?? '—'}% · ×{level.touches}{level.weeklyConfirmed ? ' · weekly ✓' : ''}</em>
              </button>
            {:else}<span class="level muted">none below price</span>{/each}
          </div>
        </div>

        {#if data.snapshot.reasons.length}
          <ul class="reasons">
            {#each data.snapshot.reasons as reason}<li>{reason}</li>{/each}
          </ul>
        {/if}

        {#if data.warnings.length}
          <div class="warnings">
            {#each data.warnings as warning}
              <p><AlertTriangle size={12} /> {warning}</p>
            {/each}
          </div>
        {/if}
      </div>
    {/if}
  {/if}
</section>

<style>
  .chart-card {
    background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
    padding: 1rem; display: flex; flex-direction: column; gap: .75rem; min-width: 0;
  }
  header { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; flex-wrap:wrap; }
  .title { display:flex; gap:.6rem; align-items:flex-start; color:var(--primary); min-width:0; }
  .title div { display:flex; flex-direction:column; gap:.2rem; min-width:0; }
  .title .instrument { flex-direction:row; align-items:baseline; gap:.45rem; flex-wrap:wrap; }
  .title b { color:var(--text); font-size:.9rem; font-family:ui-monospace, monospace; }
  .title .instrument strong { color:var(--text-2); font-size:.72rem; font-weight:500; }
  .title span { color:var(--text-3); font-size:.72rem; }
  .title em { color:var(--warning); font-style:normal; }
  .title .quote-line {
    display:flex; flex-direction:row; align-items:center; gap:.42rem; flex-wrap:wrap;
    font-variant-numeric:tabular-nums;
  }
  .price-readout {
    display:inline-flex; align-items:baseline; gap:.28rem; padding:.12rem .42rem;
    border-radius:6px; background:var(--surface-2); border:1px solid var(--border-md);
  }
  .price-readout small { color:var(--text-3); font-size:.6rem; text-transform:uppercase; letter-spacing:.035em; }
  .title .price-readout strong { color:var(--text); font-size:1rem; font-weight:750; line-height:1; }
  .quote-line .positive { color:var(--success); font-weight:700; }
  .quote-line .negative { color:var(--danger); font-weight:700; }

  .head-actions { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; }
  .intervals { display:flex; padding:2px; border:1px solid var(--border-md); border-radius:8px; background:var(--surface-2); }
  .intervals button {
    border:0; border-radius:5px; padding:.25rem .5rem; background:transparent;
    color:var(--text-3); font:inherit; font-size:.7rem; cursor:pointer;
  }
  .intervals button.active { background:var(--surface-3); color:var(--text); }
  .intervals button:disabled { opacity:.5; cursor:default; }
  .icon {
    background:none; border:0; cursor:pointer; color:var(--text-3);
    padding:.3rem; border-radius:var(--radius-sm); display:flex;
  }
  .icon:hover { background:var(--surface-2); color:var(--text); }
  .assess-btn { display:flex; align-items:center; gap:.35rem; }

  /* The price action, volume, and RSI share this canvas. Give them enough vertical separation for
     level labels and recent candles to remain legible on ordinary desktop screens. */
  .plot { width:100%; height:580px; min-width:0; flex-shrink:0; }
  /* A fixed height rather than a vh clamp: this renders inside an iframe whose viewport is shorter
     than the browser window, so a vh-based value collapsed back to its minimum and the expand button
     gained width but almost no height. The page scrolls, so a definite 680px is the honest choice. */
  .plot.tall { height:740px; }
  .plot.loading { opacity:.5; }
  .loading-state {
    display:flex; align-items:center; justify-content:center; gap:.5rem;
    color:var(--text-3); font-size:.75rem; background:var(--surface-2); border-radius:var(--radius-sm);
  }

  .history-status {
    display:grid; grid-template-columns:minmax(0,1fr) minmax(150px,240px); align-items:center;
    gap:.35rem .8rem; padding:.55rem .65rem; border-radius:var(--radius-sm);
    border:1px solid color-mix(in srgb, var(--primary) 28%, var(--border));
    background:color-mix(in srgb, var(--primary) 7%, var(--surface-2));
    color:var(--text-2); font-size:.7rem;
  }
  .history-copy { display:flex; align-items:center; gap:.5rem; min-width:0; color:var(--primary); }
  .history-copy > div { display:flex; flex-direction:column; gap:.1rem; min-width:0; }
  .history-copy b { color:var(--text); font-size:.73rem; }
  .history-copy span, .history-status small { color:var(--text-3); line-height:1.4; }
  .history-status small { grid-column:1 / -1; }
  .history-progress { display:flex; align-items:center; gap:.45rem; }
  .history-progress progress { width:100%; height:.42rem; accent-color:var(--primary); }
  .history-progress strong { color:var(--text-2); font-size:.68rem; font-variant-numeric:tabular-nums; }
  .history-progress .fetch {
    display:inline-flex; align-items:center; gap:.25rem; white-space:nowrap;
    border:1px solid var(--border-md); background:var(--surface-2); color:var(--text-2);
    border-radius:var(--radius-sm); padding:.2rem .45rem; font:inherit; font-size:.66rem; cursor:pointer;
  }
  .history-progress .fetch:hover:not(:disabled) {
    color:var(--primary); border-color:color-mix(in srgb, var(--primary) 45%, var(--border));
  }
  .history-progress .fetch:disabled { opacity:.5; cursor:wait; }
  /* Not a warm-up in progress, so it does not borrow the primary colour that means "working on it". */
  .history-status.new-listing { border-color:var(--border-md); background:var(--surface-2); }
  .history-status.new-listing .history-copy { color:var(--text-3); }
  :global(.spinning) { animation:history-spin 1s linear infinite; }
  @keyframes history-spin { to { transform:rotate(360deg); } }

  .view-controls { display:flex; gap:.25rem; }
  .view-controls button {
    display:inline-flex; align-items:center; gap:.25rem;
    border:1px solid var(--border-md); background:var(--surface-2); color:var(--text-2);
    border-radius:999px; padding:.2rem .5rem; font:inherit; font-size:.66rem; cursor:pointer;
    text-transform:lowercase; white-space:nowrap;
  }
  .view-controls button:hover { color:var(--text); border-color:var(--border-high); }
  .view-controls button.muted { color:var(--text-3); opacity:.7; }
  .view-controls button:disabled { opacity:.4; cursor:default; }

  .msg { color:var(--text-3); font-size:.78rem; margin:0; padding:1.5rem 0; text-align:center; }
  .msg.danger { color:var(--danger); }
  .inline-error {
    margin:0; padding:.4rem .55rem; border:1px solid color-mix(in srgb, var(--danger) 30%, transparent);
    border-radius:var(--radius-sm); background:color-mix(in srgb, var(--danger) 7%, transparent);
    color:var(--danger); font-size:.7rem;
  }
  .retry {
    border:0; background:none; color:inherit; font:inherit; font-weight:600;
    text-decoration:underline; cursor:pointer; padding:.1rem .2rem;
  }

  .readout { display:flex; flex-direction:column; gap:.6rem; }
  .metrics { display:grid; grid-template-columns:repeat(auto-fit,minmax(110px,1fr)); gap:.4rem; }
  .metrics div { background:var(--surface-2); border-radius:var(--radius-sm); padding:.4rem .55rem; display:flex; flex-direction:column; gap:.15rem; }
  .metrics span { color:var(--text-3); font-size:.65rem; }
  .metrics b { color:var(--text); font-size:.75rem; overflow-wrap:anywhere; }
  .metrics b.danger { color:var(--danger); }

  .plan { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; font-size:.72rem; color:var(--text-2); }
  .plan b { color:var(--text); }
  .plan .rr { color:var(--primary); font-weight:600; }
  .arm-plan {
    display:inline-flex; align-items:center; gap:.25rem;
    background:none; border:1px solid color-mix(in srgb, var(--primary) 40%, transparent);
    border-radius:999px; color:var(--primary); cursor:pointer;
    font:inherit; font-size:.68rem; padding:.1rem .45rem;
  }
  .arm-plan:hover { background:color-mix(in srgb, var(--primary) 15%, transparent); }

  .chip { display:inline-flex; align-items:center; gap:.2rem; font-size:.63rem; padding:.1rem .4rem; border-radius:999px; border:1px solid var(--border-md); color:var(--text-3); }
  .chip.ok { color:var(--success); border-color:color-mix(in srgb, var(--success) 35%, transparent); }
  .chip.warn { color:var(--warning); border-color:color-mix(in srgb, var(--warning) 35%, transparent); }
  .chip.monitor { color:var(--info); border-color:color-mix(in srgb, var(--info) 35%, transparent); }

  .drop-guard { display:flex; align-items:center; gap:.35rem; flex-wrap:wrap; }
  .drop-guard b {
    color:var(--text-3); font-size:.65rem; text-transform:uppercase; letter-spacing:.04em;
    display:inline-flex; align-items:center; gap:.25rem; margin-right:.15rem;
  }
  .drop-btn {
    background:var(--surface-2); border:1px solid var(--border-md); border-radius:var(--radius-sm);
    color:var(--danger); font:inherit; font-size:.72rem; font-weight:600; cursor:pointer;
    padding:.22rem .45rem; display:inline-flex; align-items:baseline; gap:.3rem;
    font-variant-numeric:tabular-nums;
  }
  .drop-btn:hover { border-color:var(--danger); background:var(--surface-3); }
  .drop-btn em { color:var(--text-3); font-style:normal; font-weight:400; font-size:.66rem;
                 font-family:ui-monospace, monospace; }
  .drop-hint { color:var(--text-3); font-size:.62rem; }

  .levels { display:grid; grid-template-columns:1fr 1fr; gap:.5rem; }
  .levels > div { display:flex; flex-direction:column; gap:.2rem; }
  .levels b { color:var(--text-3); font-size:.65rem; text-transform:uppercase; letter-spacing:.04em; }
  .level { font-size:.72rem; color:var(--text); font-family:ui-monospace, monospace; }
  .hint { color:var(--text-3); font-size:.6rem; font-style:normal; text-transform:none; letter-spacing:0; margin-left:.3rem; }
  button.level.armable {
    background:none; border:0; padding:.1rem .25rem; margin-left:-.25rem; text-align:left;
    cursor:pointer; border-radius:var(--radius-sm); font:inherit; font-size:.72rem;
    font-family:ui-monospace, monospace; color:var(--text); width:fit-content;
  }
  button.level.armable:hover { background:var(--surface-3); color:var(--primary); }
  .action { display:inline-flex; min-width:2.1rem; font-size:.58rem; font-weight:700; letter-spacing:.04em; }
  .action.buy { color:var(--success); }
  .action.sell { color:var(--danger); }
  .level em { color:var(--text-3); font-style:normal; font-size:.66rem; font-family:inherit; }
  .level.muted { color:var(--text-3); }

  .reasons { margin:0; padding-left:1.1rem; color:var(--text-2); font-size:.71rem; line-height:1.6; }
  .warnings p { margin:0; color:var(--warning); font-size:.69rem; display:flex; gap:.3rem; align-items:flex-start; line-height:1.5; }

  @media (max-width: 640px) {
    .chart-card { padding:.75rem; }
    header { gap:.65rem; }
    .head-actions { width:100%; gap:.35rem; }
    .intervals { max-width:100%; overflow-x:auto; }
    .intervals button { padding-inline:.42rem; }
    .plot, .plot.tall { height:clamp(300px, 88vw, 420px); }
    .metrics { grid-template-columns:repeat(auto-fit,minmax(90px,1fr)); }
    .levels { grid-template-columns:minmax(0,1fr); }
    .history-status { grid-template-columns:minmax(0,1fr); }
    .history-status small { grid-column:auto; }
  }

  @media (prefers-reduced-motion: reduce) {
    :global(.spinning) { animation:none; }
  }
</style>
