import { writable, derived } from 'svelte/store';
import type {
  AgentStatus, AgentInfo, ToolInfo, SkillInfo, ReferenceItem, PendingApprovalInfo,
  ToolActivity
} from './api';

// ── Agent status (polled every 5 s) ──────────────────────────────────────
export const agentStatus = writable<AgentStatus | null>(null);

// ── Sidebar collapsed state ───────────────────────────────────────────────
export const sidebarCollapsed = writable(false);

// ── Active conversation ID ────────────────────────────────────────────────
export const activeConversationId = writable<string | undefined>(undefined);
export const activeAgentId = writable('main');

// ── Chat history (in-memory for current session) ──────────────────────────
export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  streaming?: boolean;
  error?: string;
  timestamp: Date;
  isBackgroundResult?: boolean;
  references?: ReferenceItem[];
  pendingApproval?: PendingApprovalInfo;
  reasoning?: string;
  status?: string;
  toolActivities?: ToolActivity[];
}

export const chatMessages = writable<ChatMessage[]>([]);

export function resetChat(agentId = 'main') {
  chatMessages.set([]);
  activeConversationId.set(undefined);
  activeAgentId.set(agentId);
}

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

export function appendReasoning(id: string, text: string) {
  if (!text) return;
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, reasoning: (m.reasoning ?? '') + text } : m)
  );
}

export function setMessageStatus(id: string, status: string) {
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, status } : m)
  );
}

export function upsertToolActivity(id: string, activity: ToolActivity) {
  chatMessages.update(msgs => msgs.map(m => {
    if (m.id !== id) return m;
    const current = m.toolActivities ?? [];
    const index = current.findIndex(item => item.callId === activity.callId);
    const toolActivities = index < 0
      ? [...current, activity]
      : current.map((item, i) => i === index ? { ...item, ...activity } : item);
    return { ...m, toolActivities };
  }));
}

export function finalizeMessage(id: string, error?: string) {
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, streaming: false, error } : m)
  );
}

export function attachReferences(id: string, references?: ReferenceItem[]) {
  if (!references || references.length === 0) return;
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, references } : m)
  );
}

// Shows/updates a bubble for a HITL approval blocking the current turn (upsert by
// approvalId so repeated polls don't spawn duplicate bubbles).
export function upsertPendingApproval(approval: PendingApprovalInfo) {
  chatMessages.update(msgs => {
    const existing = msgs.find(m => m.pendingApproval?.approvalId === approval.approvalId);
    if (existing) {
      return msgs.map(m => m.id === existing.id ? { ...m, pendingApproval: approval } : m);
    }
    return [...msgs, {
      id: crypto.randomUUID(), role: 'assistant' as const, content: '',
      pendingApproval: approval, timestamp: new Date()
    }];
  });
}

// Clears a resolved approval's bubble (by id, or all of them if none is currently pending).
export function clearPendingApproval(approvalId?: string) {
  chatMessages.update(msgs =>
    msgs.map(m => (!approvalId || m.pendingApproval?.approvalId === approvalId)
      ? { ...m, pendingApproval: undefined }
      : m)
  );
}

// ── Cache stores (refreshed on page load) ────────────────────────────────
export const tools  = writable<ToolInfo[]>([]);
export const skills = writable<SkillInfo[]>([]);
export const agents = writable<AgentInfo[]>([]);

// ── Derived: is agent ready? ──────────────────────────────────────────────
export const agentReady = derived(agentStatus, s => s?.ready ?? false);
