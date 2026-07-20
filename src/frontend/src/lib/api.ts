// ── API client for AgentFox backend ──────────────────────────────────────

const BASE = '/api';

// ── Types ─────────────────────────────────────────────────────────────────

export interface ChatRequest {
  message: string;
  conversationId?: string;
}

export interface ReferenceItem {
  url: string;
  title?: string;
  source?: string;
}

export interface ChatResponse {
  response: string;
  conversationId?: string;
  success: boolean;
  error?: string;
  references?: ReferenceItem[];
}

export interface AgentStatus {
  status: string;
  name: string;
  id?: string;
  ready: boolean;
  version?: string;
  uptime: string;
}

export interface VersionInfo {
  version: string;
  full: string;
  commit: string;
  display: string;
}

export interface AgentInfo {
  id: string;
  name: string;
  status: string;
  role: 'main' | 'sub';
  subAgentCount?: number;
}

export interface SpecialistAgentInfo {
  id: string;
  name: string;
  description: string;
  isActive: boolean;
  modelKey?: string;
  toolNames: string[];
  channelTypes: string[];
  routeHints: string[];
  maxIterations: number;
  maxConcurrentTurns: number;
  waitingTurns: number;
  activeTurns: number;
  totalTurns: number;
  failedTurns: number;
  activatedUtc?: string;
  lastActivityUtc?: string;
  lastDurationMilliseconds: number;
  lastError?: string;
}

export interface CommandLaneInfo {
  lane: string;
  queuedCommands: number;
  activeCommands: number;
  maxConcurrency: number;
  handlerRegistered: boolean;
}

export interface CommandQueueStatus {
  totalQueuedCommands: number;
  processor: {
    totalProcessed: number;
    totalFailed: number;
    uptime: string;
    queuedCommands: number;
    activeCommands: number;
  };
  lanes: CommandLaneInfo[];
  checkedUtc: string;
}

export interface ToolInfo {
  name: string;
  description: string;
}

export interface SkillInfo {
  name: string;
  description: string;
  toolCount: number;
  skillType: string;
}

export interface MemoryEntry {
  id: string;
  type: string;
  content: string;
  timestamp: string;
  importance: number;
}

export interface SessionInfo {
  id: string;
  title?: string;
  memoryEnabled: boolean;
  memoryOverride?: boolean | null;
  agentId: string;
  origin: string;
  status: string;
  createdAt: string;
  lastActive: string;
  channelType?: string;
}

export interface ConversationMessage {
  role: 'user' | 'assistant';
  content: string;
  references?: ReferenceItem[];
}

export interface ConversationMessagesResponse {
  conversationId: string;
  agentId: string;
  messages: ConversationMessage[];
}

export interface McpServerInfo {
  name: string;
  toolCount: number;
  tools: string[];
  status: 'connected' | 'failed';
  error?: string;
}

export interface McpStatus {
  servers: McpServerInfo[];
  totalTools: number;
  serverCount: number;
  failureCount: number;
}

export interface HeartbeatInfo {
  name: string;
  task: string;
  intervalSeconds: number;
  maxMissed: number;
  missedCount: number;
  lastTriggered: string;
  isPaused: boolean;
  status: 'active' | 'paused';
}

export interface HeartbeatRequest {
  name: string;
  task: string;
  intervalSeconds?: number;
  maxMissed?: number;
}

export interface HeartbeatUpdateRequest {
  task?: string;
  intervalSeconds?: number;
  maxMissed?: number;
}

export interface CronJobInfo {
  name: string;
  cronExpression: string;
  task: string;
  lastExecuted: string | null;
  nextExecution: string;
}

export interface CronJobRequest {
  name: string;
  cronExpression: string;
  task: string;
}

// ── Plugin Sessions & Config ──────────────────────────────────────────────

export interface ToolExecution {
  executionId: string;
  toolName: string;
  arguments: Record<string, unknown>;
  startedAt: string;
  completedAt?: string;
  executionTimeMs: number;
  status: 'Running' | 'Completed' | 'Failed';
  result?: string;
  error?: string;
}

export interface PluginSessionSummary {
  pluginName: string;
  sessionId: string;
  createdAt: string;
  lastActivityAt: string;
  toolCount: number;
  successfulToolCount: number;
  failedToolCount: number;
}

export interface PluginSessionDetail extends PluginSessionSummary {
  executions: ToolExecution[];
}

export interface PluginSessionStats {
  pluginName: string;
  activeSessionCount: number;
  totalToolInvocations: number;
  successfulInvocations: number;
  failedInvocations: number;
  successRate: number;
}

export interface PluginConfigResponse {
  pluginName: string;
  displayName?: string;
  description?: string;
  config: Record<string, unknown>;
  fields?: PluginConfigField[];
  lastUpdatedAt: string;
  isDefault: boolean;
}

export interface PluginConfigField {
  key: string;
  label: string;
  description: string;
  type: 'string' | 'boolean' | 'number' | 'select';
  defaultValue?: unknown;
  options: string[];
  sensitive: boolean;
  runtimeEditable: boolean;
}

export interface PluginConfigUpdateRequest {
  config: Record<string, unknown>;
  merge?: boolean;
}

export interface ChannelInfo {
  id: string;
  name: string;
  type: string;
  isConnected: boolean;
  status: 'connected' | 'disconnected';
}

export interface ChannelsStatus {
  ready: boolean;
  channels: ChannelInfo[];
  total: number;
  connected: number;
}

export interface PendingNotification {
  message: string;
  timestamp: string;
  subAgentRunId?: string;
}

export interface PendingNotificationsResponse {
  conversationId: string;
  count: number;
  notifications: PendingNotification[];
}

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

// ── Helpers ───────────────────────────────────────────────────────────────

export function setManagementApiKey(key: string) {
  if (typeof sessionStorage === 'undefined') return;
  if (key.trim()) sessionStorage.setItem('agentfox.managementApiKey', key.trim());
  else sessionStorage.removeItem('agentfox.managementApiKey');
}

export function getManagementApiKey(): string {
  if (typeof sessionStorage === 'undefined') return '';
  return sessionStorage.getItem('agentfox.managementApiKey') ?? '';
}

function requestHeaders(json = false): Record<string, string> {
  const headers: Record<string, string> = {};
  if (json) headers['Content-Type'] = 'application/json';
  const key = getManagementApiKey();
  if (key) headers['X-AgentFox-Api-Key'] = key;
  return headers;
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: requestHeaders() });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function post<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method:  'POST',
    headers: requestHeaders(true),
    body:    body !== undefined ? JSON.stringify(body) : undefined
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

export type SpecialistMemoryMode = 'Shared' | 'Isolated' | 'Disabled';

export interface MemorySettings {
  globalEnabled: boolean;
  agents: Array<{
    id: string;
    name: string;
    mode: SpecialistMemoryMode;
  }>;
}

async function patch<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method:  'PATCH',
    headers: requestHeaders(true),
    body:    JSON.stringify(body)
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function del<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { method: 'DELETE', headers: requestHeaders() });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

// ── Endpoints ─────────────────────────────────────────────────────────────

export const api = {
  health:   () => get<{ status: string; version: string; timestamp: string }>('/health'),
  version:  () => get<VersionInfo>('/version'),
  status:   () => get<AgentStatus>('/status'),
  agents:   () => get<AgentInfo[]>('/agents'),
  specialistAgents: () => get<SpecialistAgentInfo[]>('/specialist-agents'),
  specialistChat: (agentId: string, message: string, conversationId?: string) =>
    post<ChatResponse>(`/specialist-agents/${encodeURIComponent(agentId)}/chat`, { message, conversationId }),
  commandQueues: () => get<CommandQueueStatus>('/command-queues'),
  tools:    () => get<ToolInfo[]>('/tools'),
  skills:   () => get<SkillInfo[]>('/skills'),
  memory:   () => get<MemoryEntry[]>('/memory'),
  memorySettings: () => get<MemorySettings>('/memory/settings'),
  setGlobalMemory: (enabled: boolean) =>
    patch<{ globalEnabled: boolean }>('/memory/settings', { enabled }),
  setSpecialistMemory: (agentId: string, mode: SpecialistMemoryMode) =>
    patch<{ agentId: string; mode: SpecialistMemoryMode }>(
      `/memory/agents/${encodeURIComponent(agentId)}`, { mode }),
  sessions: () => get<SessionInfo[]>('/sessions'),
  sessionMessages: (conversationId: string) =>
    get<ConversationMessagesResponse>(`/session-messages?conversationId=${encodeURIComponent(conversationId)}`),
  resumeSession: (conversationId: string) =>
    post<{ success: boolean; conversationId: string }>('/sessions/resume', { conversationId }),
  renameSession: (conversationId: string, title: string) =>
    patch<{ success: boolean; conversationId: string; title: string }>('/sessions', { conversationId, title }),
  setSessionMemory: (conversationId: string, enabled: boolean | null) =>
    patch<{ success: boolean; conversationId: string; memoryEnabled: boolean; memoryOverride?: boolean | null }>(
      '/sessions/memory', { conversationId, enabled }),
  importSession: (envelope: unknown) =>
    post<{ success: boolean; conversationId: string }>('/session-import', envelope),
  deleteSession: (conversationId: string) =>
    del<{ success: boolean; conversationId?: string }>(
      `/sessions?conversationId=${encodeURIComponent(conversationId)}`),
  exportSession: async (conversationId: string): Promise<void> => {
    const res = await fetch(
      `${BASE}/session-export?conversationId=${encodeURIComponent(conversationId)}`,
      { headers: requestHeaders() });
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${conversationId.replace(/\//g, '_')}.agentfox.json`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  },
  mcp:      () => get<McpStatus>('/mcp'),
  channels: () => get<ChannelsStatus>('/channels'),
  pendingNotifications: (conversationId: string) =>
    get<PendingNotificationsResponse>(`/chat/pending/${encodeURIComponent(conversationId)}`),

  chat: async (req: ChatRequest): Promise<ChatResponse> => {
    const res = await fetch(`${BASE}/chat`, {
      method:  'POST',
      headers: requestHeaders(true),
      body:    JSON.stringify(req)
    });
    return res.json();
  },

  // ── Heartbeats ───────────────────────────────────────────────────────────
  heartbeats: {
    list:   ()                                   => get<HeartbeatInfo[]>('/heartbeats'),
    add:    (req: HeartbeatRequest)              => post<{ success: boolean }>('/heartbeats', req),
    update: (name: string, req: HeartbeatUpdateRequest) =>
      post<{ success: boolean }>(`/heartbeats/${encodeURIComponent(name)}/update`, req),
    remove: (name: string)                       => del<{ success: boolean }>(`/heartbeats/${encodeURIComponent(name)}`),
    pause:  (name: string)                       => post<{ success: boolean }>(`/heartbeats/${encodeURIComponent(name)}/pause`),
    resume: (name: string)                       => post<{ success: boolean }>(`/heartbeats/${encodeURIComponent(name)}/resume`),
  },

  // ── Cron Jobs ────────────────────────────────────────────────────────────
  cron: {
    list:   ()                         => get<CronJobInfo[]>('/cron'),
    add:    (req: CronJobRequest)      => post<{ success: boolean }>('/cron', req),
    remove: (name: string)             => del<{ success: boolean }>(`/cron/${encodeURIComponent(name)}`),
  },

  // ── Plugin Sessions (audit trail & tracking) ──────────────────────────
  pluginSessions: {
    listAll:    ()                                => get<PluginSessionSummary[]>('/plugin-sessions'),
    listByPlugin: (pluginName: string)            => get<PluginSessionSummary[]>(`/plugin-sessions/${encodeURIComponent(pluginName)}`),
    getDetail:  (pluginName: string, sessionId: string) => get<PluginSessionDetail>(`/plugin-sessions/${encodeURIComponent(pluginName)}/${encodeURIComponent(sessionId)}`),
    getStats:   (pluginName: string)              => get<PluginSessionStats>(`/plugin-sessions/${encodeURIComponent(pluginName)}/stats`),
  },

  // ── Plugin Configuration (dynamic, web-ui configurable) ────────────────
  pluginConfig: {
    listAll:    ()                                => get<PluginConfigResponse[]>('/plugin-config'),
    get:        (pluginName: string)              => get<PluginConfigResponse>(`/plugin-config/${encodeURIComponent(pluginName)}`),
    update:     (pluginName: string, req: PluginConfigUpdateRequest) => post<{ success: boolean; message: string }>(`/plugin-config/${encodeURIComponent(pluginName)}`, req),
    remove:     (pluginName: string)              => del<{ success: boolean }>(`/plugin-config/${encodeURIComponent(pluginName)}`),
  },

  trading: {
    status:         ()                    => get<TradingStatus>('/trading/status'),
    proposals:      (limit = 100)         => get<TradeProposal[]>(`/trading/proposals?limit=${limit}`),
    executions:     (limit = 100)         => get<TradingExecution[]>(`/trading/executions?limit=${limit}`),
    events:         (limit = 200)         => get<TradingEvent[]>(`/trading/events?limit=${limit}`),
    reconciliation: (limit = 100)         => get<ReconciliationRun[]>(`/trading/reconciliation?limit=${limit}`),
  }
};

// ── SSE streaming chat ────────────────────────────────────────────────────
// Yields { token } events and a final { done, conversationId } event.

export type StreamEvent =
  | { type: 'token';  token: string }
  | { type: 'done';   done: true; conversationId?: string; references?: ReferenceItem[] }
  | { type: 'error';  error: string };

export async function* streamChat(
  message: string,
  conversationId?: string,
  signal?: AbortSignal
): AsyncGenerator<StreamEvent> {
  const res = await fetch(`${BASE}/chat/stream`, {
    method:  'POST',
    headers: requestHeaders(true),
    body:    JSON.stringify({ message, conversationId }),
    signal
  });

  if (!res.ok || !res.body) {
    yield { type: 'error', error: `HTTP ${res.status}` };
    return;
  }

  const reader  = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer       = '';
  let currentEvent = 'message';

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';

      for (const line of lines) {
        if (line.startsWith('event: ')) {
          currentEvent = line.slice(7).trim();
        } else if (line.startsWith('data: ')) {
          try {
            const payload = JSON.parse(line.slice(6));
            if (currentEvent === 'done') {
              yield { type: 'done', done: true, conversationId: payload.conversationId, references: payload.references };
            } else if (currentEvent === 'error') {
              yield { type: 'error', error: payload.error ?? 'Unknown error' };
            } else {
              yield { type: 'token', token: payload.token ?? '' };
            }
          } catch {
            // malformed JSON line — skip
          }
          currentEvent = 'message';
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
