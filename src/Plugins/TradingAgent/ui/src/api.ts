/**
 * Trading API client — owned by the plugin, not the host.
 *
 * These types describe THIS plugin's endpoints (mapped in TradingAgentModule.MapEndpoints), so they
 * belong next to it. They used to live in the host app's src/lib/api.ts, which meant every change to
 * a trading response shape was a change to the AgentFox frontend.
 *
 * Auth: the page is framed same-origin by the host, so it can read the management API key straight
 * out of the shared sessionStorage. The host also posts it on load (see main.ts) for the case where
 * the page is opened standalone during development.
 */

const BASE = '/api';
const KEY_STORAGE = 'agentfox.managementApiKey';

let injectedKey = '';

/** Records the key the host handed us via postMessage. */
export function setInjectedApiKey(key: string) {
  injectedKey = key?.trim() ?? '';
}

function apiKey(): string {
  if (injectedKey) return injectedKey;
  if (typeof sessionStorage === 'undefined') return '';
  return sessionStorage.getItem(KEY_STORAGE) ?? '';
}

function headers(json = false): Record<string, string> {
  const h: Record<string, string> = {};
  if (json) h['Content-Type'] = 'application/json';
  const key = apiKey();
  if (key) h['X-AgentFox-Api-Key'] = key;
  return h;
}

// A request that never resolves (a dropped connection, a starved browser connection pool sitting
// behind the live alert stream) would otherwise leave a caller's busy flag stuck forever with no way
// to recover short of a page refresh. A hard timeout turns that into an ordinary rejected promise.
const REQUEST_TIMEOUT_MS = 20_000;

// Candle reads may top up an incomplete archive from the PSX portal. That path has a 25s upstream
// attempt budget, so it cannot share the 20s CRUD ceiling without the browser giving up first.
const CANDLE_TIMEOUT_MS = 60_000;

function withTimeout(timeoutMs = REQUEST_TIMEOUT_MS): { signal: AbortSignal; cancel: () => void } {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  return { signal: controller.signal, cancel: () => clearTimeout(timer) };
}

async function get<T>(path: string, timeoutMs?: number): Promise<T> {
  const { signal, cancel } = withTimeout(timeoutMs);
  try {
    const res = await fetch(`${BASE}${path}`, { headers: headers(), signal });
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    return await (res.json() as Promise<T>);
  } catch (e) {
    if (e instanceof Error && e.name === 'AbortError') throw new Error('Request timed out.');
    throw e;
  } finally {
    cancel();
  }
}

async function send<T>(
  method: string, path: string, body?: unknown, timeoutMs?: number
): Promise<T> {
  const { signal, cancel } = withTimeout(timeoutMs);
  try {
    const res = await fetch(`${BASE}${path}`, {
      method,
      headers: headers(body !== undefined),
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal
    });
    if (!res.ok) {
      // The watchlist endpoints answer a rejected edit with { error, message }; surfacing that beats
      // showing the user a bare "400 Bad Request".
      const detail = await res.json().catch(() => null);
      throw new Error(detail?.message ?? `${res.status} ${res.statusText}`);
    }
    return await (res.json() as Promise<T>);
  } catch (e) {
    if (e instanceof Error && e.name === 'AbortError') throw new Error('Request timed out.');
    throw e;
  } finally {
    cancel();
  }
}

const post  = <T>(path: string, body?: unknown, timeoutMs?: number) =>
  send<T>('POST', path, body, timeoutMs);
const patch = <T>(path: string, body?: unknown) => send<T>('PATCH', path, body);
const del   = <T>(path: string) => send<T>('DELETE', path);

// ── Types ─────────────────────────────────────────────────────────────────

export interface TradingStatus {
  policy: {
    autoExecute: boolean;
    executionMode: string;
    minConfidence: string;
    version: string;
  };
  ledger: {
    pendingProposals: number;
    submittingExecutions: number;
    unknownExecutions: number;
    acceptedExecutions: number;
    checkedUtc: string;
  };
  market: {
    isOpen: boolean;
    pktNow: string;
    reason: string;
    nextOpenPkt?: string;
    scheduleSource: string;
    sessionOpenPkt?: string;
  };
  reconciliation: {
    supported: boolean;
    healthy: boolean;
    reason: string;
    checkedUtc: string;
    detailsJson: string;
  };
  killSwitch: boolean;
  reconciliationFresh: boolean;
  liveExecutionReady: boolean;
  checkedUtc: string;
}

/** One layman-facing choice from the server-side order intent registry. */
export interface OrderIntentDefinition {
  id: string;
  label: string;
  description: string;
  category: string;
  submission: 'immediate' | 'conditional';
  action: 'BUY' | 'SELL';
  orderType: 'LIMIT' | 'MARKET' | 'STOPLOSS';
  triggerKind: TriggerKind | null;
  priceField: 'none' | 'limit' | 'target' | 'stop' | 'limit-at-trigger' | string;
  defaultPercent: number | null;
  trailing: boolean;
}

export interface OrderIntentRegistryResponse {
  intents: OrderIntentDefinition[];
  capabilities: {
    marketOrdersEnabled: boolean;
    brokerOrderTypes: string[];
    conditionalTriggerTypes: string[];
  };
}

export interface DashboardOrderRequest {
  orderIntentId: string;
  symbol: string;
  quantity: number;
  price?: number | null;
  triggerPrice?: number | null;
  limitPrice?: number | null;
  clientRequestId: string;
  persistentUntilFilled?: boolean;
  expiresInDays?: number;
}

export interface DashboardOrderResult {
  accepted: boolean;
  isReplay: boolean;
  executionId: string;
  policyVersion: string;
  reason: string;
  groups: unknown[];
  persistentOrder?: PersistentOrder | null;
}

export interface PersistentOrderPlacement {
  placementId: string;
  intentId: string;
  sessionDate: string;
  attempt: number;
  quantity: number;
  brokerOrderNo: string | null;
  executionId: string | null;
  state: 'accepted' | 'failed' | 'unknown' | string;
  requestedPrice: number | null;
  submittedPrice: number | null;
  message: string | null;
  createdUtc: string;
}

export interface PersistentOrder {
  intentId: string;
  symbol: string;
  action: 'BUY' | 'SELL' | string;
  quantity: number;
  orderType: 'LIMIT' | 'STOPLOSS' | string;
  price: number | null;
  limitPrice: number | null;
  expiresUtc: string;
  state: string;
  filledQuantity: number;
  remainingQuantity: number;
  lastAttemptSessionDate: string | null;
  attemptCount: number;
  lastOrderNo: string | null;
  sourceArmedId: string | null;
  stateReason: string | null;
  note: string | null;
  createdUtc: string;
  updatedUtc: string;
  terminalUtc: string | null;
  canRetry: boolean;
  retryReason: string;
  placements: PersistentOrderPlacement[];
}

export interface PersistentOrdersResponse { orders: PersistentOrder[]; }

/**
 * Coverage of the local daily-candle archive that weekly support/resistance is derived from, plus
 * whatever the backfill is doing right now.
 */
export interface CandleArchiveStatus {
  backfillEnabled: boolean;
  backfillYears: number;
  configuredSymbols: number;
  archive: {
    symbols: number;
    bars: number;
    coveredDates: number;
    earliestSession?: string;
    latestSession?: string;
  };
  /**
   * Trading days not yet retrieved for *every* archived symbol. Symbol-aware on purpose: coverage is
   * recorded per (date, symbol), so a symbol added after the deep history was fetched shows up here
   * instead of the archive claiming completeness while that symbol has almost no bars.
   */
  missingTradingDays: number;
  targetTradingDays: number;
  /** Archived daily sessions a symbol needs before weekly levels can be computed from them. */
  dailyBarsForWeekly: number;
  /** Symbols below that threshold, shortest history first — each offers a targeted backfill. */
  symbolsShortOfWeekly: {
    symbol: string;
    archivedBars: number;
    /** Sessions a backfill scoped to this symbol would fetch. */
    missingSessions: number;
  }[];
  progress: {
    isRunning: boolean;
    startedUtc?: string;
    completedUtc?: string;
    datesTargeted: number;
    datesCompleted: number;
    sessionsStored: number;
    emptyDates: number;
    currentDate?: string;
    abortedForThrottling: boolean;
    message?: string;
    percentComplete?: number;
  };
}

/**
 * A proposal is what the specialist produced from a signal that arrived while nobody was watching —
 * a WhatsApp tip overnight, typically. It has a lifecycle:
 * `proposed → executing → executed | rejected | expired`, which is what makes this an inbox rather
 * than the write-only log it used to be.
 */
export interface TradeProposal {
  proposalId: string;
  status: 'proposed' | 'executing' | 'executed' | 'rejected' | 'expired' | string;
  proposal: {
    orders?: Array<Record<string, unknown>>;
    source_message?: string;
    rationale?: string;
  };
  policyVersion: string;
  createdUtc: string;
  updatedUtc: string;
  /** The execution this became, once executed. */
  executionId?: string | null;
  /** Why it was rejected or expired. */
  stateReason?: string | null;
}

export interface TradingExecution {
  executionId: string;
  state: string;
  request: unknown;
  result?: unknown;
  policyVersion: string;
  createdUtc: string;
  updatedUtc: string;
}

export interface BrokerAccountBalance {
  key: string;
  label: string;
  value?: number | null;
  currency?: string | null;
  attributes: Record<string, string | null>;
}

export interface BrokerAccountHolding {
  instrumentId: string;
  symbol?: string | null;
  exchange?: string | null;
  assetType?: string | null;
  quantity?: number | null;
  averageCost?: number | null;
  marketPrice?: number | null;
  costValue?: number | null;
  marketValue?: number | null;
  unrealizedProfitLoss?: number | null;
  unrealizedProfitLossPercent?: number | null;
  currency?: string | null;
  attributes: Record<string, string | null>;
}

export interface BrokerAccountOrder {
  orderId: string;
  externalOrderId?: string | null;
  instrumentId: string;
  symbol?: string | null;
  exchange?: string | null;
  side?: string | null;
  orderType?: string | null;
  status?: string | null;
  quantity?: number | null;
  remainingQuantity?: number | null;
  price?: number | null;
  triggerPrice?: number | null;
  currency?: string | null;
  placedAt?: string | null;
  attributes: Record<string, string | null>;
}

export interface BrokerAccountSnapshot {
  brokerId: string;
  brokerName: string;
  accountLabel?: string | null;
  balancesAvailable: boolean;
  holdingsAvailable: boolean;
  ordersAvailable: boolean;
  balances: BrokerAccountBalance[];
  holdings: BrokerAccountHolding[];
  orders: BrokerAccountOrder[];
  retrievedAtUtc: string;
  warnings: string[];
  attributes: Record<string, string | null>;
}

export interface TradingEvent {
  eventId: number;
  executionId: string;
  eventType: string;
  payload: unknown;
  createdUtc: string;
}

export interface ReconciliationRun {
  reconciliationId: string;
  state: string;
  details: unknown;
  startedUtc: string;
  completedUtc?: string;
}

/**
 * One watched symbol. `tradable` is the important one: the watchlist is deliberately wider than the
 * configured AllowedSymbols, so a symbol can be charted and alerted on while an order for it would be
 * refused by the risk engine. `hasWeeklyHistory` says whether enough daily bars are archived for
 * weekly support/resistance to mean anything yet.
 */
export interface WatchlistEntry {
  symbol: string;
  companyName?: string | null;
  /** Current session move from the previous close; absent when market-watch data is unavailable. */
  dayChangePercent?: number | null;
  addedUtc: string;
  source: 'seed' | 'user' | string;
  sortOrder: number;
  pinned: boolean;
  alertsEnabled: boolean;
  notes?: string | null;
  tradable: boolean;
  /**
   * The stored per-symbol toggle. False = manual-only: no automation may originate an order for the
   * symbol, entry or exit, while you still can. Distinct from `alertsEnabled`, which only mutes.
   */
  autoTradeEnabled: boolean;
  /** Effective answer, after `ManualOnlySymbols` from configuration is folded in. */
  manualOnly: boolean;
  /** True when the pin comes from configuration, which the API cannot lift — show it, don't offer it. */
  manualOnlyLocked: boolean;
  archivedBars: number;
  hasWeeklyHistory: boolean;
  /** Alerts still in the `new` state for this symbol — drives the row badge. */
  openAlerts: number;
}

/** One bar plus the indicator values at that bar (null until enough history exists). */
export interface ChartCandle {
  /** Seconds since epoch — what lightweight-charts expects. */
  time: number;
  date: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  isLive: boolean;
  sma20: number | null;
  sma50: number | null;
  rsi14: number | null;
}

/**
 * A horizontal price level drawn from swing pivots and range extremes. `touches` is how many pivots
 * merged into it — a level tested repeatedly is stronger than one drawn off a single bar — and
 * `weeklyConfirmed` means the weekly chart recognises it too, which is what separates structure from
 * a recent swing.
 */
export interface ChartLevel {
  price: number;
  touches: number;
  origin: string;
  weeklyConfirmed: boolean;
  distancePercent: number | null;
}

/**
 * Extra marks the backend asked us to draw. Always present and always this shape — empty for the
 * community edition, populated by a licensed edition (projections, predicted points, a next target,
 * a confidence band). The renderer is shared, so there is one drawing path whatever produced them.
 *
 * `kind` is a SEMANTIC token, never a color: the client maps it to a theme token so overlays stay
 * legible in both light and dark and an edition cannot take over the palette. An unknown kind falls
 * back to neutral rather than breaking the chart.
 */
export type ChartOverlayKind =
  | 'projection' | 'prediction' | 'target' | 'entry' | 'stop'
  | 'support' | 'resistance' | 'neutral' | string;

export interface ChartOverlayPoint { time: number; value: number; }

export interface ChartOverlays {
  levels: {
    id: string; label: string; price: number;
    kind: ChartOverlayKind; weight: number; confirmed: boolean;
  }[];
  /** A line across time; `points` may extend PAST the last candle — that is a projection. */
  series: {
    id: string; label: string; kind: ChartOverlayKind;
    dashed: boolean; points: ChartOverlayPoint[];
  }[];
  markers: {
    id: string; time: number; text: string; kind: ChartOverlayKind;
    position: 'aboveBar' | 'belowBar'; value: number | null;
  }[];
  /** Upper/lower envelope drawn as two lines — a confidence band around a projection. */
  bands: {
    id: string; label: string; kind: ChartOverlayKind;
    points: { time: number; lower: number; upper: number; }[];
  }[];
}

export interface ChartData {
  symbol: string;
  interval: string;
  /** False when an order for this symbol would be rejected by the risk engine. */
  tradable: boolean;
  barsAnalyzed: number;
  sessionsAvailable: number;
  /** True while a newly archived symbol is being rendered from the bars already available locally. */
  historyBuilding: boolean;
  /** Settled daily bars currently stored for this symbol. */
  archivedBars: number;
  /** The last bar is still forming — not a settled close. */
  usesLiveBar: boolean;
  /** RSI bands this analysis classified against (config, not the textbook 30/70). */
  thresholds: { rsiOversold: number; rsiOverbought: number };
  candles: ChartCandle[];
  levels: { supports: ChartLevel[]; resistances: ChartLevel[] };
  /** Edition overlays. Empty in the community build; see ChartOverlays. */
  overlays: ChartOverlays;
  plan: {
    entry: number | null;
    stop: number | null;
    target: number | null;
    rewardRisk: number | null;
    /**
     * Whether THIS plan's entry level is confirmed on the weekly chart. Distinct from
     * `weekly.entryLevelConfirmed`, which is computed against the full archived history and can refer
     * to a different level than the one shown for the requested window.
     */
    entryWeeklyConfirmed: boolean;
  };
  snapshot: {
    close: number;
    asOf: string;
    dayChangePercent: number | null;
    zone: string;
    setup: string;
    trend: string | null;
    rsi14: number | null;
    atr14: number | null;
    atrPercent: number | null;
    sma20: number | null;
    sma50: number | null;
    volume: number;
    averageVolume: number | null;
    volumeRatio: number | null;
    rangeLow: number;
    rangeHigh: number;
    rangePosition: number | null;
    nearestSupport: number | null;
    percentAboveSupport: number | null;
    nearestResistance: number | null;
    percentBelowResistance: number | null;
    reasons: string[];
  };
  weekly: {
    bars: number;
    alignment: string;
    breakdown: boolean;
    entryLevelConfirmed: boolean;
    zone: string | null;
    setup: string | null;
    nearestSupport: number | null;
    nearestResistance: number | null;
    notes: string[];
  };
  retrievedAtUtc: string;
  warnings: string[];
}

export const CHART_INTERVALS = ['1M', '1W', '1D', '60m', '30m', '15m', '5m'] as const;
export type ChartInterval = (typeof CHART_INTERVALS)[number];

/** An alert the monitor raised. Every kind is a transition, not a standing condition. */
export interface TradingAlert {
  alertId: string;
  symbol: string;
  kind: string;
  severity: 'Low' | 'Medium' | 'High' | 'Critical' | string;
  levelPrice: number | null;
  price: number;
  interval: string;
  summary: string;
  reasons: string[];
  /** The level is confirmed on the weekly chart — structure rather than a recent swing. */
  weeklyConfirmed: boolean;
  /** Raised off a still-forming bar, so the trigger could still un-happen before the close. */
  fromLiveBar: boolean;
  state: 'new' | 'acknowledged' | 'dismissed' | string;
  raisedUtc: string;
  sessionDate: string;
}

/**
 * A structured confidence verdict. The numbers it reasons over are deterministic; the model only
 * judges them, and `invalidationLevel` is chosen from the levels in the evidence rather than invented.
 */
export interface StockAssessment {
  confidence: 'HIGH' | 'MEDIUM' | 'LOW' | 'NONE' | string;
  confidenceScore: number;
  recommendation: 'PROCEED' | 'CAUTION' | 'AVOID' | 'INSUFFICIENT_DATA' | string;
  rationale: string;
  supportingFactors: string[];
  riskFactors: string[];
  invalidationLevel: number | null;
  /** The model that produced it, for the audit trail. */
  model: string | null;
  assessedUtc: string;
  /** Served from the session cache rather than a fresh model call. */
  fromCache: boolean;
}

interface AssessmentJobSubmission {
  jobId: string;
  state: 'queued' | 'running' | 'succeeded' | 'failed';
  reused: boolean;
}

interface AssessmentJob<T> {
  jobId: string;
  state: 'queued' | 'running' | 'succeeded' | 'failed';
  result: T | null;
  error: string | null;
}

/** Polls short status requests; the model keeps running even if this page is refreshed or closed. */
async function waitForAssessment<T>(jobId: string): Promise<T> {
  for (;;) {
    const job = await get<AssessmentJob<T>>(
      `/trading/assessment-jobs/${encodeURIComponent(jobId)}`);
    if (job.state === 'succeeded' && job.result) return job.result;
    if (job.state === 'failed') throw new Error(job.error ?? 'Assessment failed.');
    await new Promise(resolve => setTimeout(resolve, 1_000));
  }
}

/**
 * Trigger kinds an armed order can wait on.
 *
 * The percent kinds lead because they are the ones that need no chart reading: "sell if it drops 3%"
 * is a complete instruction, where "sell at 97.40" first requires knowing that 97.40 is the level
 * that matters. The exact-level kinds stay for when it is.
 */
export const TRIGGER_KINDS = [
  'PercentDrop', 'PercentRise', 'PriceBelow', 'PriceAbove', 'Event'
] as const;
export type TriggerKind = (typeof TRIGGER_KINDS)[number];

/** Percent triggers measure a move; the others wait at a fixed level or on an event. */
export const isPercentTrigger = (kind: TriggerKind | string) =>
  kind === 'PercentDrop' || kind === 'PercentRise';

/** Presets offered as one-click chips, so a size of move needs no typing. */
export const PERCENT_PRESETS = [2, 3, 5, 10] as const;

/**
 * The level a percent trigger works out to. Mirrors the backend's PercentTrigger.Level, including its
 * 2-decimal rounding — the dialog quotes this number to the operator, and a client that rounded
 * differently would quote a level the server never armed.
 */
export function percentTriggerLevel(
  kind: TriggerKind | string, reference: number | null, percent: number | null
): number | null {
  if (!isPercentTrigger(kind)) return null;
  if (!reference || reference <= 0) return null;
  if (!percent || percent <= 0 || percent > 50) return null;
  const factor = kind === 'PercentDrop' ? 1 - percent / 100 : 1 + percent / 100;
  const level = Math.round(reference * factor * 100) / 100;
  return level > 0 ? level : null;
}

/** Alert kinds an Event trigger can key off — must match the backend's AlertKind enum. */
export const ALERT_KINDS = [
  'SupportBounce', 'ResistanceRejection', 'SupportBreak', 'ResistanceBreakout',
  'SetupChanged', 'TrendFlip', 'WeeklyBreakdown', 'RsiOversold', 'RsiOverbought'
] as const;

/**
 * An order waiting on a condition. Only fires while AgentFox is running and the market is open — a
 * native broker stop has neither limitation, which the UI states rather than implies.
 */
export interface ArmedOrder {
  armedId: string;
  symbol: string;
  triggerKind: TriggerKind | string;
  /** The level as it stands NOW — a trailing trigger's moves with the price. */
  triggerPrice: number | null;
  triggerAlertKind: string | null;
  /** Percent triggers only: the size of the move, and the price it is measured from. */
  triggerPercent: number | null;
  referencePrice: number | null;
  /** The reference follows the price in the favourable direction and never moves back. */
  trailing: boolean;
  action: 'BUY' | 'SELL' | string;
  quantity: number;
  orderType: string;
  price: number | null;
  limitPrice: number | null;
  state: 'armed' | 'firing' | 'fired' | 'cancelled' | 'expired' | 'failed' | string;
  armedUtc: string;
  expiresUtc: string | null;
  firedUtc: string | null;
  executionId: string | null;
  stateReason: string | null;
  note: string | null;
  sourceAlertId: string | null;
  /** Set when this order is the local backstop for a protective stop, not an ordinary trigger. */
  protectiveStopId: string | null;
  persistentUntilFilled: boolean;
}

/**
 * A standing intent to keep a position protected — not a queued order.
 *
 * The venue clears outstanding orders at the close, so a native stop placed today does not exist
 * tomorrow. The durable thing is the intent, and a native day order is re-placed from it each
 * session while `recurring` holds.
 */
export interface ProtectiveStop {
  stopId: string;
  symbol: string;
  parentArmedId: string | null;
  stopTrigger: number;
  stopLimit: number;
  desiredQuantity: number;
  recurring: boolean;
  state: 'pending_fill' | 'active' | 'closed' | string;
  /** Holding before the entry went in. `null` means never captured — which is not zero. */
  baselineQuantity: number | null;
  placedQuantity: number;
  lastPlacedSessionDate: string | null;
  lastOrderNo: string | null;
  localBackstopArmedId: string | null;
  createdUtc: string;
  fillConfirmedUtc: string | null;
  closedUtc: string | null;
  stateReason: string | null;
  note: string | null;
  /** Whether a native stop is resting at the broker right now — the only protection that survives
   *  AgentFox being down. */
  restingToday: boolean;
}

export interface ArmedOrdersResponse {
  orders: ArmedOrder[];
  protectiveStops: ProtectiveStop[];
  approval: {
    mode: string;
    armedUntilUtc: string | null;
    armedBy: string | null;
    autoApprovedThisSession: number;
    maxOrdersPerSession: number;
  };
  caveat: string;
}

/** What the UI needs to arm an order. Levels and sizes are pre-filled from the chart or the alert. */
export interface ArmOrderRequest {
  symbol: string;
  action: 'BUY' | 'SELL';
  quantity: number;
  triggerKind: TriggerKind;
  triggerPrice?: number | null;
  triggerAlertKind?: string | null;
  orderType?: string;
  price?: number | null;
  limitPrice?: number | null;
  expiresInDays?: number;
  note?: string;
  sourceAlertId?: string | null;
  /** BUY only. Arms a protective stop that stays dormant until the entry is confirmed filled. */
  attachStop?: AttachStopRequest | null;

  // ── Percent triggers ─────────────────────────────────────────────────────
  /** Size of the move. Required for PercentDrop / PercentRise, ignored otherwise. */
  triggerPercent?: number | null;
  /**
   * The price the move is measured from. Sent explicitly so the level armed is the level the operator
   * was quoted on screen; omitted, the server captures it from the live feed.
   */
  referencePrice?: number | null;
  /** Trail the reference with the price — a drop trigger then behaves as a trailing stop. */
  trailing?: boolean;
  /** After the trigger, keep re-placing a LIMIT/STOPLOSS each trading day until filled or expired. */
  persistentUntilFilled?: boolean;
}

/** A protective stop to attach to a BUY entry. Sized at fill time, not here. */
export interface AttachStopRequest {
  stopTrigger: number;
  /** Defaults server-side to just below the trigger; a limit AT the trigger routinely misses. */
  stopLimit?: number | null;
  quantity?: number | null;
  /** Re-place the native stop each session. Off means it lapses after one day. */
  recurring: boolean;
}

/**
 * Editable values used to pre-fill the arm-order dialog from a chart level or an alert. Unlike the
 * API request, quantity is intentionally absent because the user must always choose it explicitly.
 */
export interface ArmOrderDialogContext {
  symbol: string;
  triggerKind?: TriggerKind;
  triggerPrice?: number | null;
  triggerAlertKind?: string | null;
  action?: 'BUY' | 'SELL';
  orderType?: string;
  price?: number | null;
  limitPrice?: number | null;
  sourceAlertId?: string | null;
  context?: string | null;

  // Percent-trigger pre-fill. `referencePrice` is what the caller had on screen, so the level the
  // dialog quotes is the one that gets armed rather than one re-derived a moment later.
  triggerPercent?: number | null;
  referencePrice?: number | null;
  trailing?: boolean;
  /**
   * The live price, purely so the dialog can say whether the level it is about to arm has ALREADY
   * been passed. Read-only context, never sent: it is not the same field as `referencePrice`, which
   * the user may edit away from it.
   */
  currentPrice?: number | null;

  // Protective stop pre-fill. Only meaningful on a BUY — the dialog clears it otherwise — and only
  // set by a caller that already knows the level: the chart's plan carries the entry and the stop
  // that invalidates it as one thought, so arming them separately loses the connection between them.
  attachStop?: boolean;
  stopTrigger?: number | null;
  stopLimit?: number | null;
  stopRecurring?: boolean;
}

/** One thing the trading agent did, as recorded by TradingActivityLog. */
export interface TradingActivity {
  seq: number;
  /** When it first happened. */
  utc: string;
  /** When it last happened — later than `utc` only for a collapsed repeat. */
  lastUtc: string;
  /** Further occurrences folded into this entry. 0 for something that happened once. */
  repeats: number;
  /** Which part of the agent: Broker, Orders, Stops, Armed, Monitor, Feed. */
  source: string;
  level: 'info' | 'warn' | 'error';
  message: string;
  detail: string | null;
}

/**
 * The activity feed plus the "right now" facts that no single entry can carry — chiefly whether a
 * browser window currently on screen belongs to this system.
 */
export interface TradingActivityFeed {
  lastSeq: number;
  warnings: number;
  errors: number;
  retentionMinutes: number;
  now: {
    browserBusy: boolean;
    marketOpen: boolean;
    marketReason: string;
    feedHealthy: boolean;
    monitorLastPassUtc: string | null;
  };
  activities: TradingActivity[];
}

export interface MonitorStatus {
  enabled: boolean;
  marketOpen: boolean;
  lastPassUtc: string | null;
  lastPassMs: number;
  symbolsCovered: number;
  alertsRaised: number;
  alertsSuppressed: number;
  intervalSeconds: number;
  confirmPasses: number;
  trigger: string | null;
  warnings: string[];
  message: string;
  liveSubscribers: number;
}

export interface WatchlistResponse {
  entries: WatchlistEntry[];
  seededUtc?: string | null;
  /** AllowedSymbols changed since seeding — offer a reset, never reseed silently. */
  configuredListChanged: boolean;
  /** The policy source currently controlling which symbols may pass execution risk validation. */
  executionUniverseSource: 'AllowedSymbols' | 'Watchlist';
  tradableSymbols: number;
  maxSymbols: number;
  /**
   * Symbols pinned manual-only in configuration. Includes any that are not on the watchlist at all —
   * they still block automation, so they have to be visible somewhere.
   */
  configuredManualOnly: string[];
}

export interface WatchlistPresetPreview {
  index: 'KSE100' | 'KSE30';
  label: string;
  source: string;
  sourceUrl?: string | null;
  count: number;
  alreadyWatched: number;
  missing: number;
  outsideIndex: number;
  projectedMergeCount: number;
  maxSymbols: number;
  /** True when applying the preset also changes which symbols are eligible for execution. */
  grantsTradingPermission: boolean;
  warning?: string | null;
}

export interface WatchlistPresetResult {
  index: 'KSE100' | 'KSE30';
  mode: 'merge' | 'replace';
  source: string;
  sourceUrl?: string | null;
  total: number;
  added: number;
  removed: number;
  preserved: number;
  warning?: string | null;
  message: string;
}


// ── Market movers (AHL analytics portal) ──────────────────────────────────
// Every field here is computed server-side by AhlMovers from one shared market snapshot, so the
// panel renders numbers rather than deriving them — the agent tool and this UI therefore always
// agree. `enabled` false means the portal is switched off in config; `available` false means it is
// on but the SSO handshake could not reach it (usually no broker session).

export const MOVER_SCREENS = [
  'gainers', 'losers', 'most_active', 'most_valuable', 'unusual_volume',
  'gap_up', 'gap_down', 'near_upper_cap', 'near_lower_lock'
] as const;
export type MoverScreen = typeof MOVER_SCREENS[number];

/** Human labels, kept next to the values so the picker and the heading cannot drift apart. */
export const MOVER_SCREEN_LABELS: Record<MoverScreen, string> = {
  gainers:         'Gainers',
  losers:          'Losers',
  most_active:     'Most Active',
  most_valuable:   'By Value',
  unusual_volume:  'Unusual Volume',
  gap_up:          'Gap Up',
  gap_down:        'Gap Down',
  near_upper_cap:  'At Upper Cap',
  near_lower_lock: 'At Lower Lock'
};

export interface MoverRow {
  symbol: string;
  name?: string | null;
  sectorCode?: string | null;
  sector?: string | null;
  price?: number | null;
  change?: number | null;
  changePercent?: number | null;
  volume?: number | null;
  turnoverPkr?: number | null;
  /** Today's volume as a multiple of its own 10-day average. > 2 is genuinely unusual. */
  volumeVsAvg10Day?: number | null;
  gapPercent?: number | null;
  rsi?: number | null;
  distanceToUpperCapPercent?: number | null;
  distanceToLowerLockPercent?: number | null;
  /** No headroom left in the day's band — an order beyond the cap is refused outright. */
  atUpperCap: boolean;
  atLowerLock: boolean;
  freeFloat?: number | null;
  dividendYieldPercent?: number | null;
  indices?: string[] | null;
  /** A price drop on an ex-dividend day is mechanical, not a move. Shown so it is not misread. */
  exDividend: boolean;
  exBonus: boolean;
  exRights: boolean;
  lastTickAt?: string | null;
}

export interface MarketBreadth {
  marketState?: string | null;
  lastUpdate?: string | null;
  /** Symbols that traded in the latest session, of totalListed. The rest are excluded from ranking. */
  tradedToday: number;
  totalListed: number;
  advancing: number;
  declining: number;
  unchanged: number;
  atUpperCap: number;
  atLowerLock: number;
  totalTurnoverPkr: number;
  advancingTurnoverPkr: number;
}

export interface MoversResponse {
  enabled: boolean;
  available?: boolean;
  /** True when a portal token is held — so an unavailable portal is NOT a missing broker session. */
  hasToken?: boolean;
  /** True while a failed handshake is cooling down; retrying sooner would not help. */
  handshakeCoolingDown?: boolean;
  /** The upstream status and body snippet, when the call failed. */
  error?: string | null;
  screen?: string;
  marketState?: string | null;
  asOf?: string | null;
  breadth?: MarketBreadth | null;
  rows: MoverRow[];
}

export interface SectorMove {
  sectorCode: string;
  sectorName?: string | null;
  symbols: number;
  medianChangePercent: number;
  totalTurnoverPkr: number;
  advancing: number;
  declining: number;
}

export interface SectorsResponse {
  enabled: boolean;
  available?: boolean;
  marketState?: string | null;
  asOf?: string | null;
  sectors: SectorMove[];
}

export interface MoversQuery {
  screen?: MoverScreen;
  index?: string;
  sectorCode?: string;
  limit?: number;
  minTurnover?: number;
  minPrice?: number;
}

// ── Endpoints ─────────────────────────────────────────────────────────────

export const trading = {
  status:         ()            => get<TradingStatus>('/trading/status'),
  account:        ()            => get<BrokerAccountSnapshot>('/trading/account', 60_000),
  orderIntents:   ()            => get<OrderIntentRegistryResponse>('/trading/order-intents'),
  placeOrder:     (request: DashboardOrderRequest) =>
    post<DashboardOrderResult>('/trading/orders', request, 60_000),
  persistentOrders: {
    list: (all = false) =>
      get<PersistentOrdersResponse>(`/trading/persistent-orders?all=${all}`),
    cancel: (intentId: string) =>
      del<{ intentId: string; completed: boolean; state: string; message: string }>(
        `/trading/persistent-orders/${encodeURIComponent(intentId)}`),
    retry: (intentId: string) =>
      post<{ intentId: string; placed: boolean; state: string; message: string; executionId: string | null }>(
        `/trading/persistent-orders/${encodeURIComponent(intentId)}/retry`, undefined, 60_000),
    resolveAttention: (
      intentId: string,
      resolution: 'not_filled' | 'partial' | 'filled',
      filledQuantity: number | null,
      note: string
    ) =>
      post<{ intentId: string; applied: boolean; state: string; message: string }>(
        `/trading/persistent-orders/${encodeURIComponent(intentId)}/resolve-attention`,
        { resolution, filledQuantity, note })
  },
  // Open-only by default: an empty inbox is the normal state, and a list dominated by last month's
  // resolved proposals is exactly what made this feel like a log rather than a queue.
  proposals: (openOnly = true, limit = 100) =>
    get<TradeProposal[]>(`/trading/proposals?openOnly=${openOnly}&limit=${limit}`),

  /** Hands the proposal's orders to the deterministic manager. Adds no new execution path. */
  executeProposal: (proposalId: string) =>
    post<{
      proposalId: string;
      status: string;
      accepted: boolean;
      isReplay: boolean;
      executionId: string;
      reason: string;
    }>(`/trading/proposals/${encodeURIComponent(proposalId)}/execute`),

  rejectProposal: (proposalId: string, reason?: string) =>
    post<{ proposalId: string; status: string }>(
      `/trading/proposals/${encodeURIComponent(proposalId)}/reject`, { reason }),
  executions:     (limit = 100) => get<TradingExecution[]>(`/trading/executions?limit=${limit}`),
  resolveUnknownExecution: (executionId: string, resolution: 'placed' | 'not_placed', note: string) =>
    post<{ executionId: string; state: string; resolvedUtc: string }>(
      `/trading/executions/${encodeURIComponent(executionId)}/resolve`, { resolution, note }),
  events:         (limit = 200) => get<TradingEvent[]>(`/trading/events?limit=${limit}`),
  reconciliation: (limit = 100) => get<ReconciliationRun[]>(`/trading/reconciliation?limit=${limit}`),
  reconcileNow: () => post<{
    sessionEstablished: boolean;
    reconciliation: TradingStatus['reconciliation'];
  }>('/trading/reconciliation/run', undefined, 60_000),

  setKillSwitch: (active: boolean, reason?: string) =>
    post<{ killSwitch: boolean }>('/trading/kill-switch', { active, reason }),

  candleArchive: () => get<CandleArchiveStatus>('/trading/candle-archive'),

  // Returns as soon as the pass has STARTED — a two-year backfill runs for ~18 minutes, so the
  // caller polls candleArchive() for progress instead of awaiting completion.
  //
  // `symbols` scopes the pass to the dates those symbols are missing rather than to which symbols get
  // stored (a session fetch returns the whole market either way). It is the only way to fill a symbol
  // added after the deep history was archived: every date is already on record, so an unscoped pass
  // finds nothing to do. Symbols must already be in the archive universe.
  startBackfill: (years?: number, symbols?: string[]) =>
    post<{ started: boolean; status: CandleArchiveStatus }>(
      '/trading/candle-archive/backfill', { years, symbols }),

  /** Bars, indicator lines, levels, and the level-anchored plan — one request per chart render. */
  candles: (symbol: string, interval: ChartInterval = '1D', bars?: number) => {
    const query = new URLSearchParams({ symbol, interval });
    if (bars) query.set('bars', String(bars));
    return get<ChartData>(`/trading/candles?${query}`, CANDLE_TIMEOUT_MS);
  },

  alerts: {
    list:    (opts: { symbol?: string; state?: string; limit?: number } = {}) => {
      const query = new URLSearchParams();
      if (opts.symbol) query.set('symbol', opts.symbol);
      if (opts.state) query.set('state', opts.state);
      if (opts.limit) query.set('limit', String(opts.limit));
      const suffix = query.toString();
      return get<TradingAlert[]>(`/trading/alerts${suffix ? `?${suffix}` : ''}`);
    },
    ack:     (id: string) => post<{ alertId: string; state: string }>(`/trading/alerts/${id}/ack`),
    dismiss: (id: string) => post<{ alertId: string; state: string }>(`/trading/alerts/${id}/dismiss`),
    bulk: (action: 'acknowledge' | 'dismiss', alertIds?: string[], all = false) =>
      post<{ changed: number; state: string }>('/trading/alerts/bulk', { action, alertIds, all }),

    /**
     * Live stream of new alerts. Uses fetch rather than EventSource because the /api group requires
     * the management API key header, which EventSource cannot send. Returns a stop function.
     */
    stream: (
      onAlert: (alert: TradingAlert) => void,
      onConnectionChange?: (connected: boolean) => void
    ): (() => void) => {
      const controller = new AbortController();
      let stopped = false;
      let retryTimer: ReturnType<typeof setTimeout> | null = null;

      (async () => {
        let retryMs = 1_000;
        while (!stopped) {
          let connected = false;
          try {
            const res = await fetch(`${BASE}/trading/alerts/stream`, {
              headers: headers(),
              signal: controller.signal
            });
            if (!res.ok || !res.body) throw new Error(`Alert stream returned ${res.status}`);
            // Reported on connect, not on the first alert: the indicator means "the stream is open",
            // and a quiet market is the normal case.
            connected = true;
            retryMs = 1_000;
            onConnectionChange?.(true);

            const reader = res.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';

            while (!stopped) {
              const { done, value } = await reader.read();
              if (done) break;
              // Normalise CRLF so proxy/platform newline choices cannot prevent frame detection.
              buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n');

              // SSE frames are separated by a blank line; anything after the last one is a partial
              // frame and stays in the buffer.
              const frames = buffer.split('\n\n');
              buffer = frames.pop() ?? '';
              for (const frame of frames) {
                const line = frame.split('\n').find(l => l.startsWith('data: '));
                if (!line) continue;
                try {
                  onAlert(JSON.parse(line.slice(6)) as TradingAlert);
                } catch {
                  /* malformed frame: skip it rather than tearing down the stream */
                }
              }
            }
          } catch {
            // Authentication may arrive just after the iframe mounts, and long-lived connections can
            // be dropped by sleep or a proxy. Retry below; SQLite remains the durable source of truth.
          } finally {
            if (connected) onConnectionChange?.(false);
          }

          if (stopped) break;
          await new Promise<void>(resolve => {
            retryTimer = setTimeout(resolve, retryMs);
          });
          retryTimer = null;
          retryMs = Math.min(retryMs * 2, 15_000);
        }
      })();

      return () => {
        stopped = true;
        if (retryTimer) clearTimeout(retryTimer);
        controller.abort();
      };
    }
  },

  monitor: {
    status: () => get<MonitorStatus>('/trading/monitor/status'),
    run:    () => post<MonitorStatus>('/trading/monitor/run')
  },

  /**
   * Recent activity, newest first.
   *
   * Always the whole retained window rather than an incremental fetch: the server collapses a
   * repeated activity into the existing entry, so a client holding its own copy would go on showing
   * a count that had since moved. The window is small and self-pruning, which is what makes reading
   * all of it the cheaper option.
   */
  activity: (limit?: number) =>
    get<TradingActivityFeed>('/trading/activity' + (limit ? `?limit=${limit}` : '')),

  armed: {
    list: (all = false) => get<ArmedOrdersResponse>(`/trading/armed-orders?all=${all}`),

    /**
     * `willFireUnattended` comes from the backend's own approval gate rather than being inferred
     * client-side — the execution mode matters as much as the approval mode, and guessing produced a
     * message that said the opposite of the truth.
     */
    arm: (request: ArmOrderRequest) =>
      post<{
        armedId: string;
        order: ArmedOrder;
        willFireUnattended: boolean;
        note: string;
        attachedStop: {
          stopId: string;
          stopTrigger: number;
          stopLimit: number;
          recurring: boolean;
          state: string;
          note: string;
        } | null;
      }>('/trading/armed-orders', request),

    disarm: (armedId: string) =>
      del<{ armedId: string; state: string }>(
        `/trading/armed-orders/${encodeURIComponent(armedId)}`)
  },

  stops: {
    /**
     * Stops managing the intent. It does NOT retract an order already resting at the broker — that
     * is impossible from here — so the reply says whether one is still live and needs cancelling in
     * the portal by hand.
     */
    disarm: (stopId: string) =>
      del<{
        stopId: string;
        state: string;
        brokerOrderStillResting: boolean;
        message: string;
      }>(`/trading/protective-stops/${encodeURIComponent(stopId)}`)
  },

  approval: {
    /** Opens a confirmation-free window. The reply says whether it is actually IN FORCE. */
    openWindow: (minutes?: number) =>
      post<{
        grantedUntilUtc: string;
        inForce: boolean;
        armedUntilUtc: string | null;
        note: string | null;
      }>('/trading/approval/arm', { minutes }),

    closeWindow: () => post<{ armed: boolean }>('/trading/approval/disarm')
  },

  /**
   * On-demand LLM confidence. Never called automatically — a model call per alert would cost real
   * money and hit rate limits on a busy day, and most alerts are read and dismissed without needing
   * one. Repeat calls for the same symbol+level+session are served from the server-side cache.
   */
  assess: {
    symbol: async (symbol: string, interval = '1D', context?: string) => {
      const job = await post<AssessmentJobSubmission>(
        '/trading/assessment-jobs', { symbol, interval, context });
      return waitForAssessment<{ symbol: string; assessment: StockAssessment; evidence: unknown }>(job.jobId);
    },
    alert: async (alertId: string) => {
      const job = await post<AssessmentJobSubmission>(
        `/trading/alerts/${alertId}/assessment-jobs`);
      return waitForAssessment<{
        alertId: string; symbol: string; kind: string; assessment: StockAssessment
      }>(job.jobId);
    }
  },

  watchlist: {
    /** Skip portal metadata for the first paint; a follow-up refresh can merge names and live moves. */
    list:   (includeMarketData = true) => get<WatchlistResponse>(
      `/trading/watchlist?includeMarketData=${includeMarketData}`),
    add:    (symbol: string) => post<{
                                  symbol: string;
                                  added: boolean;
                                  tradable: boolean;
                                  manualOnly: boolean;
                                  message?: string | null;
                                  warning?: string | null;
                                }>('/trading/watchlist', { symbol }),
    remove: (symbol: string) =>
      del<{ symbol: string; removed: boolean }>(`/trading/watchlist/${encodeURIComponent(symbol)}`),
    update: (
      symbol: string,
      changes: {
        alertsEnabled?: boolean;
        notes?: string;
        pinned?: boolean;
        /** False = manual-only. A true here cannot lift a pin from `ManualOnlySymbols`; the response says so. */
        autoTradeEnabled?: boolean;
      }
    ) =>
      patch<{
        symbol: string;
        updated: boolean;
        manualOnly?: boolean;
        message?: string | null;
      }>(`/trading/watchlist/${encodeURIComponent(symbol)}`, changes),
    setAutoTrading: (autoTradeEnabled: boolean) =>
      patch<{
        autoTradeEnabled: boolean;
        updated: number;
        manualOnlyLocked: number;
        message?: string | null;
      }>('/trading/watchlist/automation', { autoTradeEnabled }),
    reorder: (symbols: string[]) =>
      post<{ reordered: boolean; symbols: number }>('/trading/watchlist/reorder', { symbols }),
    reset:  ()               => post<{ symbols: number }>('/trading/watchlist/reset'),
    previewPreset: (index: 'KSE100' | 'KSE30') =>
      get<WatchlistPresetPreview>(`/trading/watchlist/presets/${index}`),
    applyPreset: (index: 'KSE100' | 'KSE30', mode: 'merge' | 'replace') =>
      post<WatchlistPresetResult>(`/trading/watchlist/presets/${index}`, { mode })
  },

  movers: {
    list: (q: MoversQuery = {}) => {
      const p = new URLSearchParams();
      if (q.screen)      p.set('screen', q.screen);
      if (q.index)       p.set('index', q.index);
      if (q.sectorCode)  p.set('sectorCode', q.sectorCode);
      if (q.limit)       p.set('limit', String(q.limit));
      if (q.minTurnover) p.set('minTurnover', String(q.minTurnover));
      if (q.minPrice)    p.set('minPrice', String(q.minPrice));
      return get<MoversResponse>(`/trading/movers?${p}`);
    },
    sectors: (index?: string) =>
      get<SectorsResponse>(`/trading/movers/sectors${index ? `?index=${encodeURIComponent(index)}` : ''}`)
  }
};
