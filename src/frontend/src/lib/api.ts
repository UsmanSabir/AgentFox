// ── API client for AgentFox backend ──────────────────────────────────────

const BASE = '/api';

// ── Types ─────────────────────────────────────────────────────────────────

/** One file attached to a turn, carried inline as base64 (no separate upload round-trip). */
export interface ChatAttachment {
  name: string;
  mediaType: string;
  /** Base64 file bytes, without a `data:` URI prefix. */
  data: string;
}

export interface ChatRequest {
  message: string;
  conversationId?: string;
  attachments?: ChatAttachment[];
}

/** What the configured model accepts as input — drives whether the UI offers attachments. */
export interface AttachmentCapabilities {
  enabled: boolean;
  images: boolean;
  documents: boolean;
  textFiles: boolean;
  maxFileSizeBytes: number;
  maxFilesPerMessage: number;
  maxTotalBytes: number;
  acceptedMediaTypes: string[];
  provider: string;
  model: string;
  source: 'config' | 'detected' | string;
}

export interface Capabilities {
  attachments: AttachmentCapabilities;
}

export interface ReferenceItem {
  url: string;
  title?: string;
  source?: string;
}

export interface ToolActivity {
  callId: string;
  toolName: string;
  status: 'running' | 'completed' | 'failed' | string;
  durationMs?: number;
}

export interface ToolActivityDetails extends ToolActivity {
  arguments?: unknown;
  result?: unknown;
}

export interface TodoItem {
  id: string;
  title: string;
  completed: boolean;
}

export interface TodoSnapshot {
  enabled: boolean;
  phase?: string;
  plan?: string;
  items: TodoItem[];
  remainingCount: number;
}

export interface ChatResponse {
  response: string;
  conversationId?: string;
  success: boolean;
  error?: string;
  references?: ReferenceItem[];
  assistantIndex?: number;
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
  forkedFromSessionId?: string;
  forkedAtAssistantIndex?: number;
}

export interface ConversationMessage {
  role: 'user' | 'assistant';
  content: string;
  agentAddition?: string;
  references?: ReferenceItem[];
  assistantIndex?: number;
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

export interface CronJobUpdateRequest {
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

/**
 * `subagent_result` — the background sub-agent's own report.
 * `agent_response`  — the agent's turn reacting to that report (a normal assistant reply).
 * `notice`          — a delivery problem worth showing the user.
 */
export type PendingNotificationKind = 'subagent_result' | 'agent_response' | 'notice';

export interface PendingNotification {
  message: string;
  timestamp: string;
  subAgentRunId?: string;
  kind?: PendingNotificationKind;
}

export interface PendingApprovalInfo {
  approvalId: string;
  trigger: string;
  description: string;
  details: string;
}

export interface PendingNotificationsResponse {
  conversationId: string;
  count: number;
  notifications: PendingNotification[];
  pendingApproval: PendingApprovalInfo | null;
}

export interface PluginUiPageInfo {
  slug: string;
  title: string;
  icon: string;
  description: string;
  order: number;
  path: string;
  entry: string;
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

async function put<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method:  'PUT',
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
  capabilities: () => get<Capabilities>('/capabilities'),
  agents:   () => get<AgentInfo[]>('/agents'),
  specialistAgents: () => get<SpecialistAgentInfo[]>('/specialist-agents'),
  specialistChat: (agentId: string, message: string, conversationId?: string) =>
    post<ChatResponse>(`/specialist-agents/${encodeURIComponent(agentId)}/chat`, { message, conversationId }),
  commandQueues: () => get<CommandQueueStatus>('/command-queues'),
  steerChat: (conversationId: string, runId: string) =>
    post<{ ok: boolean; runId: string }>('/chat/steer', { conversationId, runId }),
  cancelChat: (conversationId: string) =>
    post<{ ok: boolean }>('/chat/cancel', { conversationId }),
  tools:    () => get<ToolInfo[]>('/tools'),
  skills:   () => get<SkillInfo[]>('/skills'),
  memory:   () => get<MemoryEntry[]>('/memory'),
  deleteMemory: (id: string) => del<{ deleted: string }>(`/memory/${encodeURIComponent(id)}`),
  clearMemory: () => del<{ cleared: boolean }>('/memory'),
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
  forkSession: (conversationId: string, assistantIndex: number) =>
    post<{
      success: boolean;
      conversationId: string;
      sourceConversationId: string;
      assistantIndex: number;
    }>('/sessions/fork', { conversationId, assistantIndex }),
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
  todos: (conversationId: string) =>
    get<TodoSnapshot>(`/sessions/${encodeURIComponent(conversationId)}/todos`),
  activity: (conversationId: string) =>
    get<ToolActivity[]>(`/sessions/${encodeURIComponent(conversationId)}/activity`),
  activityDetails: (conversationId: string, callId: string) =>
    get<ToolActivityDetails>(
      `/sessions/${encodeURIComponent(conversationId)}/activity/${encodeURIComponent(callId)}`),
  hitlApprove: (approvalId: string, message?: string) =>
    post<{ ok: boolean }>(`/hitl/${encodeURIComponent(approvalId)}/approve`, { message }),
  hitlReject: (approvalId: string, message?: string) =>
    post<{ ok: boolean }>(`/hitl/${encodeURIComponent(approvalId)}/reject`, { message }),

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
    update: (name: string, req: CronJobUpdateRequest) =>
              put<{ success: boolean }>(`/cron/${encodeURIComponent(name)}`, req),
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

  // ── Plugin-supplied UI pages ───────────────────────────────────────────
  // Plugins ship their own web UI (assets served by the host at /ext/{slug}); this endpoint is the
  // only thing the host frontend knows about them. Deliberately generic: no plugin-specific types,
  // endpoints, or npm dependencies live in this app.
  pluginUi: {
    list: () => get<PluginUiPageInfo[]>('/plugin-ui'),
  }
};

// ── SSE streaming chat ────────────────────────────────────────────────────
// Yields { token } events and a final { done, conversationId } event.

export type StreamEvent =
  | { type: 'token';  token: string }
  | { type: 'session'; conversationId: string }
  | { type: 'queued'; runId: string; position: number }
  | { type: 'started'; runId: string }
  | { type: 'interrupted'; runId: string }
  | { type: 'reasoning'; text: string }
  | { type: 'status'; status: string }
  | { type: 'tool_activity'; activity: ToolActivity }
  | {
      type: 'done';
      done: true;
      runId?: string;
      conversationId?: string;
      references?: ReferenceItem[];
      assistantIndex?: number;
    }
  | { type: 'error';  error: string };

export async function* streamChat(
  message: string,
  conversationId?: string,
  signal?: AbortSignal,
  attachments?: ChatAttachment[]
): AsyncGenerator<StreamEvent> {
  const res = await fetch(`${BASE}/chat/stream`, {
    method:  'POST',
    headers: requestHeaders(true),
    body:    JSON.stringify({ message, conversationId, attachments }),
    signal
  });

  if (!res.ok || !res.body) {
    // The server rejects unusable attachments with a 400 and an explanation; surface it
    // instead of the bare status code, which would leave the user guessing.
    const detail = await res.json().catch(() => null);
    yield { type: 'error', error: detail?.error ?? `HTTP ${res.status}` };
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
              yield {
                type: 'done',
                done: true,
                runId: payload.runId,
                conversationId: payload.conversationId,
                references: payload.references,
                assistantIndex: payload.assistantIndex
              };
            } else if (currentEvent === 'session') {
              yield { type: 'session', conversationId: payload.conversationId };
            } else if (currentEvent === 'queued') {
              yield {
                type: 'queued',
                runId: payload.runId,
                position: payload.position ?? 0
              };
            } else if (currentEvent === 'started') {
              yield { type: 'started', runId: payload.runId };
            } else if (currentEvent === 'interrupted') {
              yield { type: 'interrupted', runId: payload.runId };
            } else if (currentEvent === 'reasoning') {
              yield { type: 'reasoning', text: payload.text ?? '' };
            } else if (currentEvent === 'status') {
              yield { type: 'status', status: payload.status ?? '' };
            } else if (currentEvent === 'tool_activity') {
              yield { type: 'tool_activity', activity: payload as ToolActivity };
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

/** One frame off the conversation event stream: the SSE event name and its parsed payload. */
export interface ConversationStreamEvent {
  event: string;
  data: any;
}

/**
 * Long-lived stream of live conversation events — currently the parent-session turn a finishing
 * background sub-agent triggers, which has no chat request of its own to stream into.
 *
 * Uses `fetch` rather than `EventSource` because the whole `/api` group is authorized together
 * and `EventSource` cannot send the management API key header; the alternative would be putting
 * the key in a URL, where it ends up in logs. Reconnection is the caller's job — this returns
 * when the connection closes, cleanly or otherwise.
 */
export async function* streamConversationEvents(
  conversationId: string,
  signal?: AbortSignal
): AsyncGenerator<ConversationStreamEvent> {
  const res = await fetch(`${BASE}/chat/events/${encodeURIComponent(conversationId)}`, {
    headers: requestHeaders(),
    signal
  });

  if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

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
        // `: ping` keep-alive comments and blank separators fall through untouched.
        if (line.startsWith('event: ')) {
          currentEvent = line.slice(7).trim();
        } else if (line.startsWith('data: ')) {
          try { yield { event: currentEvent, data: JSON.parse(line.slice(6)) }; }
          catch { /* malformed frame — skip it rather than kill the stream */ }
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
