// ── Codex (the owner's own ChatGPT subscription) — /api/ai/codex ──────────────────────────────
// No response carries a credential: the sign-in is the Codex CLI's own file on the server.

import { managementCall } from './claudeCode.ts';

export type CodexState = 'ready' | 'not-installed' | 'not-allowed' | 'not-signed-in' | 'reauthentication-required' | 'rate-limited';

export interface CodexStatus {
  state: CodexState;
  installed: boolean;
  version: string | null;
  allowed: boolean;
  notAllowedReason: string | null;
  signedIn: boolean;
  signedInWith: string | null;
  model: string;
  models: string[];
  reasoningEffort: string | null;
  lastAuthCheckUtc: string | null;
  lastSuccessUtc: string | null;
  lastModel: string | null;
  lastErrorUtc: string | null;
  lastError: string | null;
  lastTestUtc: string | null;
  lastTestPassed: boolean | null;
}

export type CodexSignInState = 'Idle' | 'Starting' | 'AwaitingApproval' | 'Completed' | 'Failed' | 'Expired' | 'Cancelled';

export interface CodexSignIn {
  state: CodexSignInState;
  link: string | null;
  code: string | null;
  error: string | null;
  expiresUtc: string | null;
}

const BASE = '/api/ai/codex';
const call = <T,>(method: string, path: string, body?: unknown) => managementCall<T>(method, `${BASE}${path}`, body);

export const codex = {
  status: (refresh = false) => call<CodexStatus>('GET', refresh ? '?refresh=true' : ''),
  saveSettings: (model: string, reasoningEffort: string | null) =>
    call<CodexStatus>('PUT', '/settings', { model, reasoningEffort }),
  test: () => call<{ passed: boolean; message: string; status: CodexStatus }>('POST', '/test'),
  startSignIn: () => call<CodexSignIn>('POST', '/signin'),
  signIn: () => call<CodexSignIn>('GET', '/signin'),
  cancelSignIn: () => call<CodexSignIn>('DELETE', '/signin'),
  signOut: () => call<CodexStatus>('POST', '/signout'),
};

// ── AI routing — /api/ai/routing ──────────────────────────────────────────────────────────────

export interface RoutingPurpose { id: string; label: string; provider: string; inUse: string }
export interface RoutingProvider { id: string; label: string; signedIn: boolean; allowed: boolean }
export interface Routing { purposes: RoutingPurpose[]; providers: RoutingProvider[] }

export const aiRouting = {
  read: () => managementCall<Routing>('GET', '/api/ai/routing'),
  set: (purpose: string, provider: string) => managementCall<Routing>('PUT', '/api/ai/routing', { purpose, provider }),
};
