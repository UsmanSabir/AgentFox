// ── API client for AgentFox backend ──────────────────────────────────────

const BASE = '/api';

// ── Types ─────────────────────────────────────────────────────────────────

export interface ChatRequest {
  message: string;
  conversationId?: string;
}

export interface ChatResponse {
  response: string;
  conversationId?: string;
  success: boolean;
  error?: string;
}

export interface AgentStatus {
  status: string;
  name: string;
  id?: string;
  ready: boolean;
  uptime: string;
}

export interface AgentInfo {
  id: string;
  name: string;
  status: string;
  role: 'main' | 'sub';
  subAgentCount?: number;
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
  agentId: string;
  origin: string;
  status: string;
  createdAt: string;
  lastActive: string;
  channelType?: string;
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

// ── Helpers ───────────────────────────────────────────────────────────────

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function post<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method:  'POST',
    headers: { 'Content-Type': 'application/json' },
    body:    body !== undefined ? JSON.stringify(body) : undefined
  });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

async function del<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json() as Promise<T>;
}

// ── Endpoints ─────────────────────────────────────────────────────────────

export const api = {
  health:   () => get<{ status: string; timestamp: string }>('/health'),
  status:   () => get<AgentStatus>('/status'),
  agents:   () => get<AgentInfo[]>('/agents'),
  tools:    () => get<ToolInfo[]>('/tools'),
  skills:   () => get<SkillInfo[]>('/skills'),
  memory:   () => get<MemoryEntry[]>('/memory'),
  sessions: () => get<SessionInfo[]>('/sessions'),
  mcp:      () => get<McpStatus>('/mcp'),

  chat: async (req: ChatRequest): Promise<ChatResponse> => {
    const res = await fetch(`${BASE}/chat`, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
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
  }
};

// ── SSE streaming chat ────────────────────────────────────────────────────
// Yields { token } events and a final { done, conversationId } event.

export type StreamEvent =
  | { type: 'token';  token: string }
  | { type: 'done';   done: true; conversationId?: string }
  | { type: 'error';  error: string };

export async function* streamChat(
  message: string,
  conversationId?: string,
  signal?: AbortSignal
): AsyncGenerator<StreamEvent> {
  const res = await fetch(`${BASE}/chat/stream`, {
    method:  'POST',
    headers: { 'Content-Type': 'application/json' },
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
              yield { type: 'done', done: true, conversationId: payload.conversationId };
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
