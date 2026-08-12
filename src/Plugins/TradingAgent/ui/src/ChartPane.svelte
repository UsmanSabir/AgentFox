<script lang="ts">
  import { onDestroy, tick } from 'svelte';
  import {
    createChart,
    CandlestickSeries,
    HistogramSeries,
    LineSeries,
    createSeriesMarkers,
    type IChartApi,
    type ISeriesApi,
    type IPriceLine,
    type UTCTimestamp
  } from 'lightweight-charts';
  import { trading, CHART_INTERVALS, type ChartData, type ChartInterval, type StockAssessment } from './api';
  import { LineChart, AlertTriangle, Eye, RefreshCw, Brain, Maximize2, Minimize2, Activity } from 'lucide-svelte';
  import AssessmentCard from './AssessmentCard.svelte';

  export let symbol: string | null = null;

  /** Full-width mode: the chart takes the row and the watchlist stacks beneath it. Bound by the parent. */
  export let expanded = false;

  /**
   * Incremented by the dashboard on its own timer. The parent owns the clock so there is ONE interval
   * for the page rather than one per component, and so the market-open flag driving it stays fresh.
   */
  export let refreshTick = 0;

  /** Whether the market is currently open — a closed market has nothing new to fetch. */
  export let marketOpen = false;

  /**
   * How much level detail to draw. Six labelled price lines plus the axis chips is more than a small
   * pane can carry legibly, so this trades completeness for readability:
   *   all → the nearest three each side   key → weekly-confirmed only   off → clean price action
   * The full list is always in the legend below the chart, so nothing is actually hidden from view.
   */
  let levelMode: 'all' | 'key' | 'off' = 'all';

  /** RSI in its own pane costs ~90px of candles; worth reclaiming when the pane is small. */
  let showRsi = true;

  const LEVEL_MODES = ['all', 'key', 'off'] as const;

  /** On-demand verdict for the charted symbol. Cleared when the symbol or interval changes. */
  let assessment: StockAssessment | null = null;
  let assessing = false;

  async function assess() {
    if (!symbol || assessing) return;
    assessing = true;
    error = null;
    try {
      const result = await trading.assess.symbol(symbol, interval);
      assessment = result.assessment;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      assessing = false;
    }
  }

  let interval: ChartInterval = '1D';
  let data: ChartData | null = null;
  let loading = false;
  let error: string | null = null;

  let container: HTMLDivElement | null = null;
  let chart: IChartApi | null = null;
  let candleSeries: ISeriesApi<'Candlestick'> | null = null;
  let volumeSeries: ISeriesApi<'Histogram'> | null = null;
  let sma20Series: ISeriesApi<'Line'> | null = null;
  let sma50Series: ISeriesApi<'Line'> | null = null;
  let rsiSeries: ISeriesApi<'Line'> | null = null;
  let priceLines: IPriceLine[] = [];
  let markers: ReturnType<typeof createSeriesMarkers> | null = null;
  let resizeObserver: ResizeObserver | null = null;

  // Set from the response before the chart is built, so the RSI bands drawn are the ones the backend
  // actually classified against. The defaults match TradingScanOptions.
  let rsiOversold = 35;
  let rsiOverbought = 70;

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
        borderColor: grid, timeVisible: interval !== '1D', secondsVisible: false, rightOffset: 6
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
      color: token('--text-3', '#5f6377')
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

    chart.applyOptions({ timeScale: { timeVisible: d.interval !== '1D', secondsVisible: false } });

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
    chart.timeScale().fitContent();
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
    loading = true;
    error = null;
    // A verdict belongs to the symbol and interval it was produced for; carrying it across a change
    // would attach one stock's judgement to another's chart.
    if (!keepAssessment) assessment = null;
    try {
      data = await trading.candles(symbol, interval);
      rsiOversold = data.thresholds.rsiOversold;
      rsiOverbought = data.thresholds.rsiOverbought;
      render(data);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      data = null;
    } finally {
      loading = false;
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

  // The container only exists once a symbol is selected, so rendering waits for the element rather
  // than the fetch.
  $: if (container && data && !chart) render(data);

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
        <b>{symbol ?? 'Chart'}</b>
        {#if data}
          <span>
            {data.snapshot.close} · {pct(data.snapshot.dayChangePercent)} ·
            {data.snapshot.setup} · {data.barsAnalyzed} bars
            {#if data.usesLiveBar} · <em>live bar forming</em>{/if}
          </span>
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
      </div>

      <button
        class="btn btn-ghost assess-btn"
        title="Ask the model how much confidence the evidence supports (one model call)"
        on:click={assess}
        disabled={assessing || loading || !symbol}
      ><Brain size={13} /> {assessing ? 'Assessing…' : 'Assess'}</button>
      <button class="icon" title="Refresh" on:click={load} disabled={loading || !symbol}>
        <RefreshCw size={13} />
      </button>
      <button
        class="icon"
        title={expanded ? 'Collapse — put the watchlist back beside the chart' : 'Expand — full width, taller, watchlist moves below'}
        on:click={toggleExpand}
        disabled={!symbol}
      >
        {#if expanded}<Minimize2 size={13} />{:else}<Maximize2 size={13} />{/if}
      </button>
    </div>
  </header>

  {#if !symbol}
    <p class="msg">Select a symbol from the watchlist.</p>
  {:else if error}
    <p class="msg danger">{error}</p>
  {:else}
    <div class="plot" class:loading class:tall={expanded} bind:this={container}></div>

    {#if data}
      <div class="readout">
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
          </div>
        {/if}

        <div class="levels">
          <div>
            <b>Resistance</b>
            {#each data.levels.resistances.slice(0, 3) as level}
              <span class="level">
                {level.price}
                <em>{pct(level.distancePercent)} · ×{level.touches}{level.weeklyConfirmed ? ' · weekly ✓' : ''}</em>
              </span>
            {:else}<span class="level muted">none above price</span>{/each}
          </div>
          <div>
            <b>Support</b>
            {#each data.levels.supports.slice(0, 3) as level}
              <span class="level">
                {level.price}
                <em>−{level.distancePercent ?? '—'}% · ×{level.touches}{level.weeklyConfirmed ? ' · weekly ✓' : ''}</em>
              </span>
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
  .title b { color:var(--text); font-size:.9rem; font-family:ui-monospace, monospace; }
  .title span { color:var(--text-3); font-size:.72rem; }
  .title em { color:var(--warning); font-style:normal; }

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

  /* Taller than the original 340px: six labelled price lines plus an RSI pane needs the room, and
     the axis chips overlap each other below roughly 400px. Expanded scales with the viewport. */
  .plot { width:100%; height:400px; min-width:0; }
  /* A fixed height rather than a vh clamp: this renders inside an iframe whose viewport is shorter
     than the browser window, so a vh-based value collapsed back to its minimum and the expand button
     gained width but almost no height. The page scrolls, so a definite 560px is the honest choice. */
  .plot.tall { height:560px; }
  .plot.loading { opacity:.5; }

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

  .readout { display:flex; flex-direction:column; gap:.6rem; }
  .metrics { display:grid; grid-template-columns:repeat(auto-fit,minmax(110px,1fr)); gap:.4rem; }
  .metrics div { background:var(--surface-2); border-radius:var(--radius-sm); padding:.4rem .55rem; display:flex; flex-direction:column; gap:.15rem; }
  .metrics span { color:var(--text-3); font-size:.65rem; }
  .metrics b { color:var(--text); font-size:.75rem; overflow-wrap:anywhere; }
  .metrics b.danger { color:var(--danger); }

  .plan { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; font-size:.72rem; color:var(--text-2); }
  .plan b { color:var(--text); }
  .plan .rr { color:var(--primary); font-weight:600; }

  .chip { display:inline-flex; align-items:center; gap:.2rem; font-size:.63rem; padding:.1rem .4rem; border-radius:999px; border:1px solid var(--border-md); color:var(--text-3); }
  .chip.ok { color:var(--success); border-color:color-mix(in srgb, var(--success) 35%, transparent); }
  .chip.warn { color:var(--warning); border-color:color-mix(in srgb, var(--warning) 35%, transparent); }
  .chip.monitor { color:var(--info); border-color:color-mix(in srgb, var(--info) 35%, transparent); }

  .levels { display:grid; grid-template-columns:1fr 1fr; gap:.5rem; }
  .levels > div { display:flex; flex-direction:column; gap:.2rem; }
  .levels b { color:var(--text-3); font-size:.65rem; text-transform:uppercase; letter-spacing:.04em; }
  .level { font-size:.72rem; color:var(--text); font-family:ui-monospace, monospace; }
  .level em { color:var(--text-3); font-style:normal; font-size:.66rem; font-family:inherit; }
  .level.muted { color:var(--text-3); }

  .reasons { margin:0; padding-left:1.1rem; color:var(--text-2); font-size:.71rem; line-height:1.6; }
  .warnings p { margin:0; color:var(--warning); font-size:.69rem; display:flex; gap:.3rem; align-items:flex-start; line-height:1.5; }
</style>
