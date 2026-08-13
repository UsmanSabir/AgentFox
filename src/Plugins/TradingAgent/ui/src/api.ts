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

// Model-backed endpoints get their own ceiling. 20s is a sane bound for CRUD against the local
// database, but an assessment is a full LLM round-trip over a large evidence bundle — against a
// local model that routinely takes minutes. Aborting at 20s killed the socket mid-generation, which
// surfaced as a TaskCanceledException from the OpenAI pipeline and threw away work already paid for.
const MODEL_TIMEOUT_MS = 180_000;

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
  missingTradingDays: number;
  targetTradingDays: number;
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
  addedUtc: string;
  source: 'seed' | 'user' | string;
  alertsEnabled: boolean;
  notes?: string | null;
  tradable: boolean;
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

export interface ChartData {
  symbol: string;
  interval: string;
  /** False when an order for this symbol would be rejected by the risk engine. */
  tradable: boolean;
  barsAnalyzed: number;
  sessionsAvailable: number;
  /** The last bar is still forming — not a settled close. */
  usesLiveBar: boolean;
  /** RSI bands this analysis classified against (config, not the textbook 30/70). */
  thresholds: { rsiOversold: number; rsiOverbought: number };
  candles: ChartCandle[];
  levels: { supports: ChartLevel[]; resistances: ChartLevel[] };
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

export const CHART_INTERVALS = ['1D', '60m', '30m', '15m', '5m'] as const;
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

/** Trigger kinds an armed order can wait on. */
export const TRIGGER_KINDS = ['PriceBelow', 'PriceAbove', 'Event'] as const;
export type TriggerKind = (typeof TRIGGER_KINDS)[number];

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
  triggerPrice: number | null;
  triggerAlertKind: string | null;
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
}

export interface ArmedOrdersResponse {
  orders: ArmedOrder[];
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
  tradableSymbols: number;
  maxSymbols: number;
}

// ── Endpoints ─────────────────────────────────────────────────────────────

export const trading = {
  status:         ()            => get<TradingStatus>('/trading/status'),
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
  events:         (limit = 200) => get<TradingEvent[]>(`/trading/events?limit=${limit}`),
  reconciliation: (limit = 100) => get<ReconciliationRun[]>(`/trading/reconciliation?limit=${limit}`),

  setKillSwitch: (active: boolean, reason?: string) =>
    post<{ killSwitch: boolean }>('/trading/kill-switch', { active, reason }),

  candleArchive: () => get<CandleArchiveStatus>('/trading/candle-archive'),

  // Returns as soon as the pass has STARTED — a two-year backfill runs for ~18 minutes, so the
  // caller polls candleArchive() for progress instead of awaiting completion.
  startBackfill: (years?: number) =>
    post<{ started: boolean; status: CandleArchiveStatus }>('/trading/candle-archive/backfill', { years }),

  /** Bars, indicator lines, levels, and the level-anchored plan — one request per chart render. */
  candles: (symbol: string, interval: ChartInterval = '1D', bars?: number) => {
    const query = new URLSearchParams({ symbol, interval });
    if (bars) query.set('bars', String(bars));
    return get<ChartData>(`/trading/candles?${query}`);
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

    /**
     * Live stream of new alerts. Uses fetch rather than EventSource because the /api group requires
     * the management API key header, which EventSource cannot send. Returns a stop function.
     */
    stream: (
      onAlert: (alert: TradingAlert) => void,
      onConnectionChange?: (connected: boolean) => void
    ): (() => void) => {
      const controller = new AbortController();

      (async () => {
        try {
          const res = await fetch(`${BASE}/trading/alerts/stream`, {
            headers: headers(),
            signal: controller.signal
          });
          if (!res.ok || !res.body) return;
          // Reported on connect, not on the first alert: the indicator means "the stream is open",
          // and a quiet market is the normal case.
          onConnectionChange?.(true);

          const reader = res.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });

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
          // Aborted or dropped. The alert list is re-read on the next load; SQLite is the durable path.
        } finally {
          onConnectionChange?.(false);
        }
      })();

      return () => controller.abort();
    }
  },

  monitor: {
    status: () => get<MonitorStatus>('/trading/monitor/status'),
    run:    () => post<MonitorStatus>('/trading/monitor/run')
  },

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
      }>('/trading/armed-orders', request),

    disarm: (armedId: string) =>
      del<{ armedId: string; state: string }>(
        `/trading/armed-orders/${encodeURIComponent(armedId)}`)
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
    symbol: (symbol: string, interval = '1D', context?: string) =>
      post<{ symbol: string; assessment: StockAssessment; evidence: unknown }>(
        '/trading/assess', { symbol, interval, context }, MODEL_TIMEOUT_MS),
    alert: (alertId: string) =>
      post<{ alertId: string; symbol: string; kind: string; assessment: StockAssessment }>(
        `/trading/alerts/${alertId}/assess`, undefined, MODEL_TIMEOUT_MS)
  },

  watchlist: {
    list:   ()               => get<WatchlistResponse>('/trading/watchlist'),
    add:    (symbol: string) => post<{
                                  symbol: string;
                                  added: boolean;
                                  tradable: boolean;
                                  message?: string | null;
                                  warning?: string | null;
                                }>('/trading/watchlist', { symbol }),
    remove: (symbol: string) =>
      del<{ symbol: string; removed: boolean }>(`/trading/watchlist/${encodeURIComponent(symbol)}`),
    update: (symbol: string, changes: { alertsEnabled?: boolean; notes?: string }) =>
      patch<{ symbol: string; updated: boolean }>(
        `/trading/watchlist/${encodeURIComponent(symbol)}`, changes),
    reset:  ()               => post<{ symbols: number }>('/trading/watchlist/reset')
  }
};
