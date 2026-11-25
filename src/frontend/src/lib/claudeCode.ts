// ── Claude Code (the owner's own subscription) — /api/ai/claude-code ─────────────────────────
// No response carries the token; the browser only ever SENDS one (paste), never receives it.

import { getManagementApiKey } from './api.ts';

export interface ClaudeCodeUsage {
  status: string | null;
  fiveHourPercent: number | null;
  fiveHourResetsUtc: string | null;
  sevenDayPercent: number | null;
  sevenDayResetsUtc: string | null;
  readUtc: string;
}

export type ClaudeCodeState =
  | 'ready' | 'not-installed' | 'not-allowed' | 'not-signed-in' | 'reauthentication-required'
  | 'expired' | 'rate-limited' | 'expiring-soon';

export interface ClaudeCodeStatus {
  state: ClaudeCodeState;
  installed: boolean;
  version: string | null;
  allowed: boolean;
  notAllowedReason: string | null;
  signedIn: boolean;
  tokenSource: 'signin' | 'paste' | null;
  tokenSavedUtc: string | null;
  tokenExpiresEstimateUtc: string | null;
  expiringSoon: boolean;
  model: string;
  models: string[];
  signInSupported: boolean;
  lastAuthCheckUtc: string | null;
  authMethod: string | null;
  lastSuccessUtc: string | null;
  lastModel: string | null;
  lastErrorUtc: string | null;
  lastError: string | null;
  lastTestUtc: string | null;
  lastTestPassed: boolean | null;
  usage: ClaudeCodeUsage | null;
  problem: string | null;
}

export type SignInState =
  | 'Idle' | 'Starting' | 'AwaitingCode' | 'Exchanging' | 'Completed' | 'Failed' | 'Expired' | 'Cancelled';

export interface SignInSnapshot {
  state: SignInState;
  link: string | null;
  error: string | null;
  expiresUtc: string | null;
}

export interface ClaudeCodeChoices {
  model: string;
}

const BASE = '/api/ai/claude-code';

/** One management call with the API's own `{ error }` text on failure. Shared by the AI provider cards. */
export async function managementCall<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const key = getManagementApiKey();
  if (key) headers['X-AgentFox-Api-Key'] = key;
  const res = await fetch(url, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const data = await res.json();
      if (data && typeof data.error === 'string' && data.error) message = data.error;
    } catch { /* not JSON */ }
    if (res.status === 403) message = 'Only an administrator can change this.';
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

const call = <T,>(method: string, path: string, body?: unknown) => managementCall<T>(method, `${BASE}${path}`, body);

export const claudeCode = {
  status: (refresh = false) => call<ClaudeCodeStatus>('GET', refresh ? '?refresh=true' : ''),
  saveChoices: (choices: ClaudeCodeChoices) => call<ClaudeCodeStatus>('PUT', '/settings', choices),
  saveToken: (token: string) => call<ClaudeCodeStatus>('PUT', '/token', { token }),
  removeToken: () => call<ClaudeCodeStatus>('DELETE', '/token'),
  test: () => call<{ passed: boolean; message: string; status: ClaudeCodeStatus }>('POST', '/test'),
  startSignIn: () => call<SignInSnapshot>('POST', '/signin'),
  signIn: () => call<SignInSnapshot>('GET', '/signin'),
  submitCode: (code: string) => call<SignInSnapshot>('POST', '/signin/code', { code }),
  cancelSignIn: () => call<SignInSnapshot>('DELETE', '/signin'),
};
