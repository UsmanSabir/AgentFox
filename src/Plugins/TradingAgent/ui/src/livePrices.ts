import { getContext, setContext } from 'svelte';
import { writable, type Readable, type Writable } from 'svelte/store';

export type LivePriceFreshness = 'live' | 'stale' | 'closed' | 'unknown';

/**
 * What the venue itself says it is doing, when a provider reports it. Distinct from `marketOpen`,
 * which collapses this to the one question a price display needs answering: should the tape be
 * moving? Pre-open accepts orders without matching any, so it is NOT open by that measure even
 * though an order placed then is perfectly valid.
 *
 * 'unknown' means no provider reading was served and `marketOpen` came from the trading calendar.
 */
export type VenuePhase = 'Trading' | 'PreOpen' | 'Closed' | 'Unknown';
export type LivePriceConnectionState =
  | 'unavailable'
  | 'connecting'
  | 'live'
  | 'reconnecting'
  | 'stopped';

export interface LivePrice {
  symbol: string;
  market: string;
  current: number | null;
  previousClose: number | null;
  open: number | null;
  high: number | null;
  low: number | null;
  changePercent: number | null;
  volume: number | null;
  tradeCount: number | null;
  lastTradeTime: string | null;
  boardState: string | null;
  source: string;
  receivedAtUtc: string;
}

export interface LivePriceEnvelope {
  type: 'snapshot' | 'quotes' | 'status';
  sequence: number;
  serverTimeUtc: string;
  marketOpen: boolean;
  feedState: string;
  feedReason: string;
  staleAfterSeconds: number;
  quotes: LivePrice[];
  /** Absent from providers that cannot read the venue; treated as 'Unknown'. */
  venuePhase?: VenuePhase;
  /** The provider's raw state token, for a tooltip. Never parsed here. */
  venueState?: string | null;
}

export interface LivePriceView {
  quote: LivePrice | null;
  freshness: LivePriceFreshness;
  /**
   * Carried on the view rather than read separately so `livePriceLabel` stays a pure function of
   * one argument. It is one page-wide fact copied onto every view, not a per-symbol reading.
   */
  phase: VenuePhase;
}

/** One price cell's resolved contents. See `resolveDisplayQuote`. */
export interface LivePriceDisplay {
  price: number | null;
  change: number | null;
  /** Provenance of `price`, or null when it needs no qualifier because it is a current live tick. */
  tag: 'stale' | 'pre-open' | 'close' | 'delayed' | null;
  /** Long-form provenance for a tooltip and the accessible name. */
  label: string;
}

export interface LivePriceConnection {
  state: LivePriceConnectionState;
  reason: string | null;
  feedState: string | null;
  lastMessageAtUtc: string | null;
  /** The venue's own phase, when the provider reports one. */
  venuePhase: VenuePhase;
  /** Its raw token, for a tooltip. */
  venueState: string | null;
  /**
   * True once nothing has arrived for longer than a transport that is working would ever be quiet.
   *
   * <p>`state: 'live'` is set by any envelope and latched: nothing un-set it, so a stream that
   * stopped delivering silently — a half-open socket, a proxy idle timeout, a resumed laptop — left
   * the status reading "live" indefinitely while the quote book emptied under the retention sweep
   * below. That is the failure this flag exists to make visible: a display saying prices are live
   * while showing none is worse than one that admits it has lost the feed.</p>
   *
   * <p>Measured on OUR OWN clock from when each envelope arrived, never by differencing the server's
   * `serverTimeUtc` against ours. Those are two clocks, and comparing them has already produced one
   * confident and entirely fictional finding in this product.</p>
   */
  stale: boolean;
  /**
   * How long the stream has been quiet, once that silence is a fault worth naming — 0 whenever
   * `stale` is false, and null until a first message has ever arrived.
   */
  silentForSeconds: number | null;
}

const CONTEXT_KEY = Symbol('trading-live-prices');
const UI_RETENTION_MS = 10 * 60 * 1000;
const SWEEP_MS = 15_000;
const COMMIT_MS = 100;
/**
 * Three missed heartbeats. The premium stream sends a `status` envelope every 15 seconds even on a
 * dead-quiet tape (`PremiumEndpoints.MapPremiumEndpoints`'s `/quotes/stream` PeriodicTimer), so
 * silence past this is a transport fault rather than a quiet market. Any transport feeding this book
 * must heartbeat faster than this; one that cannot has to report its own liveness instead.
 */
const SILENCE_MS = 45_000;

const cleanSymbol = (symbol: string | null | undefined) => symbol?.trim().toUpperCase() ?? '';
const positive = (value: number | null | undefined) =>
  typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : null;
const whole = (value: number | null | undefined) =>
  typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;

/**
 * One page-wide, provider-neutral quote book. Consumers subscribe by symbol, so a burst for FCCL
 * does not invalidate every watchlist row and portfolio holding. Network transport deliberately
 * lives outside this class; premium supplies AHL SSE while community remains a no-provider fallback.
 */
export class LivePriceBook {
  private readonly quotes = new Map<string, LivePrice>();
  private readonly stores = new Map<string, Writable<LivePriceView>>();
  private readonly pending = new Map<string, LivePrice>();
  private readonly connectionStore = writable<LivePriceConnection>({
    state: 'unavailable', reason: null, feedState: null, lastMessageAtUtc: null,
    venuePhase: 'Unknown', venueState: null, stale: false, silentForSeconds: null
  });
  private connectionValue: LivePriceConnection = {
    state: 'unavailable', reason: null, feedState: null, lastMessageAtUtc: null,
    venuePhase: 'Unknown', venueState: null, stale: false, silentForSeconds: null
  };
  /** When the last envelope reached US. See `LivePriceConnection.stale` for why it is not the server's stamp. */
  private lastMessageAtMs: number | null = null;
  private latestSequence = 0;
  private marketOpen = false;
  private staleAfterMs = 120_000;
  private commitTimer: ReturnType<typeof setTimeout> | null = null;
  private sweepTimer: ReturnType<typeof setInterval> | null = null;

  readonly connection: Readable<LivePriceConnection> = { subscribe: this.connectionStore.subscribe };

  start(): () => void {
    if (!this.sweepTimer)
      this.sweepTimer = setInterval(() => this.sweep(), SWEEP_MS);
    return () => this.stop();
  }

  stop(): void {
    if (this.commitTimer) clearTimeout(this.commitTimer);
    if (this.sweepTimer) clearInterval(this.sweepTimer);
    this.commitTimer = null;
    this.sweepTimer = null;
    this.pending.clear();
    this.setConnection('stopped');
  }

  quote(symbol: string | null | undefined): Readable<LivePriceView> {
    const key = cleanSymbol(symbol);
    let store = this.stores.get(key);
    if (!store) {
      store = writable(this.view(this.quotes.get(key) ?? null));
      this.stores.set(key, store);
    }
    return { subscribe: store.subscribe };
  }

  setConnection(state: LivePriceConnectionState, reason: string | null = null): void {
    // `stale` is the derived answer for a transport that has NOT noticed it stopped delivering. Once
    // the transport says so itself, the derived flag would only double-report it — and would outlive
    // the reconnect it describes, because nothing but an envelope clears it.
    const stale = state === 'live' && this.connectionValue.stale;
    this.connectionValue = { ...this.connectionValue, state, reason, stale };
    this.connectionStore.set(this.connectionValue);
    this.refreshViews();
  }

  /**
   * Applies one stream envelope. False means a delta gap was detected; the transport must reconnect
   * for a snapshot rather than silently leaving a symbol at an old value.
   */
  ingest(envelope: LivePriceEnvelope): boolean {
    if (!Number.isFinite(envelope.sequence) || envelope.sequence < 0) return true;

    if (envelope.type === 'quotes'
        && this.latestSequence > 0
        && envelope.sequence > this.latestSequence + 1) return false;
    if (envelope.type === 'quotes' && envelope.sequence <= this.latestSequence) return true;

    this.marketOpen = envelope.marketOpen;
    this.staleAfterMs = Math.max(30_000, envelope.staleAfterSeconds * 1000);
    this.lastMessageAtMs = Date.now();
    this.connectionValue = {
      ...this.connectionValue,
      state: 'live',
      reason: envelope.feedReason || null,
      feedState: envelope.feedState || null,
      lastMessageAtUtc: envelope.serverTimeUtc,
      venuePhase: envelope.venuePhase ?? 'Unknown',
      venueState: envelope.venueState ?? null,
      // A heartbeat counts. It carries no quotes, but it proves the transport is still delivering,
      // which is the only thing this pair of fields claims.
      stale: false,
      silentForSeconds: 0
    };
    this.connectionStore.set(this.connectionValue);

    if (envelope.type === 'status') {
      this.refreshViews();
      return true;
    }

    if (envelope.type === 'snapshot') {
      this.latestSequence = envelope.sequence;
      this.pending.clear();
      if (this.commitTimer) clearTimeout(this.commitTimer);
      this.commitTimer = null;

      const next = new Map<string, LivePrice>();
      for (const raw of envelope.quotes ?? []) {
        const quote = this.normalize(raw);
        if (quote) next.set(quote.symbol, quote);
      }
      this.quotes.clear();
      for (const [symbol, quote] of next) this.quotes.set(symbol, quote);
      this.refreshViews();
      return true;
    }

    this.latestSequence = envelope.sequence;
    for (const raw of envelope.quotes ?? []) {
      const quote = this.normalize(raw);
      if (quote) this.pending.set(quote.symbol, quote);
    }
    if (this.pending.size && !this.commitTimer)
      this.commitTimer = setTimeout(() => this.flush(), COMMIT_MS);
    return true;
  }

  private normalize(raw: LivePrice): LivePrice | null {
    const symbol = cleanSymbol(raw.symbol);
    if (!symbol) return null;
    return {
      symbol,
      market: cleanSymbol(raw.market) || 'REG',
      current: positive(raw.current),
      previousClose: positive(raw.previousClose),
      open: positive(raw.open),
      high: positive(raw.high),
      low: positive(raw.low),
      changePercent: typeof raw.changePercent === 'number' && Number.isFinite(raw.changePercent)
        ? raw.changePercent : null,
      volume: whole(raw.volume),
      tradeCount: whole(raw.tradeCount),
      lastTradeTime: raw.lastTradeTime || null,
      boardState: raw.boardState || null,
      source: raw.source || 'live',
      receivedAtUtc: raw.receivedAtUtc
    };
  }

  private flush(): void {
    this.commitTimer = null;
    const changed: string[] = [];
    for (const [symbol, quote] of this.pending) {
      this.quotes.set(symbol, quote);
      changed.push(symbol);
    }
    this.pending.clear();
    for (const symbol of changed) this.stores.get(symbol)?.set(this.view(this.quotes.get(symbol)!));
  }

  private sweep(): void {
    this.refreshSilence();
    const cutoff = Date.now() - UI_RETENTION_MS;
    const removed: string[] = [];
    for (const [symbol, quote] of this.quotes) {
      const received = Date.parse(quote.receivedAtUtc);
      if (!Number.isFinite(received) || received < cutoff) {
        this.quotes.delete(symbol);
        removed.push(symbol);
      }
    }
    for (const symbol of removed) this.stores.get(symbol)?.set(this.view(null));
    // Fresh values can cross the stale threshold without another trade, so refresh subscribed rows.
    for (const [symbol, store] of this.stores)
      if (!removed.includes(symbol)) store.set(this.view(this.quotes.get(symbol) ?? null));
  }

  /**
   * Recomputes whether the stream has gone quiet, and publishes it when the answer changes.
   *
   * <p>Runs on the sweep's existing tick, so its resolution is `SWEEP_MS`. A background tab whose
   * timers the browser has throttled therefore notices late — acceptable, because it corrects on the
   * next tick after focus, and because a hidden tab is not a screen anyone is trading from.</p>
   */
  private refreshSilence(): void {
    if (this.lastMessageAtMs === null) return;
    const silentMs = Date.now() - this.lastMessageAtMs;
    const stale = this.connectionValue.state === 'live' && silentMs > SILENCE_MS;
    // Only a stall needs a duration. Publishing a counter that ticked every sweep would re-render
    // every subscriber of this store forever, to say nothing anyone is waiting to hear.
    const silentForSeconds = stale ? Math.max(0, Math.round(silentMs / 1000)) : 0;
    if (stale === this.connectionValue.stale
        && silentForSeconds === this.connectionValue.silentForSeconds) return;

    this.connectionValue = { ...this.connectionValue, stale, silentForSeconds };
    this.connectionStore.set(this.connectionValue);
  }

  private refreshViews(): void {
    for (const [symbol, store] of this.stores)
      store.set(this.view(this.quotes.get(symbol) ?? null));
  }

  private view(quote: LivePrice | null): LivePriceView {
    const phase = this.connectionValue.venuePhase;
    if (!quote) return { quote: null, freshness: 'unknown', phase };
    // Pre-open lands here too, via marketOpen:false. That is correct rather than a shortcut: the
    // book is not matching, so the last traded price IS the right thing to show and calling it
    // delayed would report a fault the venue is not having. `phase` carries the nuance to the label.
    if (!this.marketOpen) return { quote, freshness: 'closed', phase };
    const received = Date.parse(quote.receivedAtUtc);
    const feedCurrent = !this.connectionValue.feedState
      || !['disabled', 'idle', 'waiting-for-data', 'disconnected'].includes(this.connectionValue.feedState);
    const current = Number.isFinite(received)
      && Date.now() - received <= this.staleAfterMs
      && this.connectionValue.state === 'live'
      && !this.connectionValue.stale
      && feedCurrent;
    return { quote, freshness: current ? 'live' : 'stale', phase };
  }
}

const unavailableBook = new LivePriceBook();

export function provideLivePrices(book: LivePriceBook): void {
  setContext(CONTEXT_KEY, book);
}

export function useLivePrices(): LivePriceBook {
  return getContext<LivePriceBook | undefined>(CONTEXT_KEY) ?? unavailableBook;
}

/**
 * What one price cell should show, once the live book and the caller's delayed fallbacks are both
 * taken into account.
 *
 * <p>Pure, and shared by every consumer, because the rule it encodes is easy to get subtly wrong and
 * was: a cell resolves price and change INDEPENDENTLY, so a row can legitimately show a live change
 * beside a delayed price, or a delayed change beside no price at all. `tag` therefore describes where
 * the PRICE came from rather than where the view came from — a live quote whose own `current` is null
 * still has to label a fallback price as delayed.</p>
 *
 * <p>The tag is not decoration. A delayed snapshot rendered without it is indistinguishable from a
 * live tick, which would make a stalled feed look like a quiet market.</p>
 */
export function resolveDisplayQuote(
  view: LivePriceView,
  fallbackPrice: number | null = null,
  fallbackChange: number | null = null,
  showPrice = true
): LivePriceDisplay {
  const livePrice = view.quote?.current ?? null;
  const price = livePrice ?? fallbackPrice;
  const change = view.quote?.changePercent ?? fallbackChange;

  const tag = livePrice !== null
    ? (view.freshness === 'live'
        ? null
        // Pre-open is not "close": the price is the last trade, but the venue is live and taking
        // orders. Labelling it "close" reads as a market that has finished for the day.
        : view.freshness === 'stale' ? 'stale' : view.phase === 'PreOpen' ? 'pre-open' : 'close')
    // Conditioned on a price being SHOWN, not merely on there being one, so a change-only column
    // does not repeat a tag its sibling cell already carries.
    : showPrice && price !== null ? 'delayed' : null;

  return { price, change, tag, label: view.quote ? livePriceLabel(view) : 'Delayed market snapshot' };
}

export function livePriceLabel(view: LivePriceView): string {
  if (!view.quote) return 'Price unavailable';
  if (view.freshness === 'closed')
    return view.phase === 'PreOpen'
      ? 'Pre-open — last traded price, orders queue until the open'
      : 'Last traded price';
  if (view.freshness === 'stale') return 'Last received price — feed delayed';
  return 'Live price';
}
