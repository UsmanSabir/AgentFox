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

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: headers() });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function post<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: headers(true),
    body: body !== undefined ? JSON.stringify(body) : undefined
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

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

export interface TradeProposal {
  proposalId: string;
  status: string;
  proposal: {
    orders?: Array<Record<string, unknown>>;
    source_message?: string;
    rationale?: string;
  };
  policyVersion: string;
  createdUtc: string;
  updatedUtc: string;
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

// ── Endpoints ─────────────────────────────────────────────────────────────

export const trading = {
  status:         ()            => get<TradingStatus>('/trading/status'),
  proposals:      (limit = 100) => get<TradeProposal[]>(`/trading/proposals?limit=${limit}`),
  executions:     (limit = 100) => get<TradingExecution[]>(`/trading/executions?limit=${limit}`),
  events:         (limit = 200) => get<TradingEvent[]>(`/trading/events?limit=${limit}`),
  reconciliation: (limit = 100) => get<ReconciliationRun[]>(`/trading/reconciliation?limit=${limit}`),

  setKillSwitch: (active: boolean, reason?: string) =>
    post<{ killSwitch: boolean }>('/trading/kill-switch', { active, reason }),

  candleArchive: () => get<CandleArchiveStatus>('/trading/candle-archive'),

  // Returns as soon as the pass has STARTED — a two-year backfill runs for ~18 minutes, so the
  // caller polls candleArchive() for progress instead of awaiting completion.
  startBackfill: (years?: number) =>
    post<{ started: boolean; status: CandleArchiveStatus }>('/trading/candle-archive/backfill', { years })
};
