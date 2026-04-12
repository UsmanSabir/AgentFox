import { writable, derived } from 'svelte/store';
import type { AgentStatus, AgentInfo, ToolInfo, SkillInfo } from './api';

// ── Agent status (polled every 5 s) ──────────────────────────────────────
export const agentStatus = writable<AgentStatus | null>(null);

// ── Sidebar collapsed state ───────────────────────────────────────────────
export const sidebarCollapsed = writable(false);

// ── Active conversation ID ────────────────────────────────────────────────
export const activeConversationId = writable<string | undefined>(undefined);

// ── Chat history (in-memory for current session) ──────────────────────────
export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  streaming?: boolean;
  error?: string;
  timestamp: Date;
  isBackgroundResult?: boolean;
}

export const chatMessages = writable<ChatMessage[]>([]);

export function addUserMessage(content: string): string {
  const id = crypto.randomUUID();
  chatMessages.update(msgs => [...msgs, {
    id, role: 'user', content, timestamp: new Date()
  }]);
  return id;
}

export function addAssistantMessage(content = '', streaming = false): string {
  const id = crypto.randomUUID();
  chatMessages.update(msgs => [...msgs, {
    id, role: 'assistant', content, streaming, timestamp: new Date()
  }]);
  return id;
}

export function addBackgroundResultMessage(content: string): string {
  const id = crypto.randomUUID();
  chatMessages.update(msgs => [...msgs, {
    id, role: 'assistant', content, streaming: false,
    isBackgroundResult: true, timestamp: new Date()
  }]);
  return id;
}

export function appendToken(id: string, token: string) {
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, content: m.content + token } : m)
  );
}

export function finalizeMessage(id: string, error?: string) {
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, streaming: false, error } : m)
  );
}

// ── Cache stores (refreshed on page load) ────────────────────────────────
export const tools  = writable<ToolInfo[]>([]);
export const skills = writable<SkillInfo[]>([]);
export const agents = writable<AgentInfo[]>([]);

// ── Derived: is agent ready? ──────────────────────────────────────────────
export const agentReady = derived(agentStatus, s => s?.ready ?? false);
