import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readWorkspaceAppearance, saveWorkspaceAppearance, APPEARANCE_RETENTION_MS } from '../src/workspaceAppearance.ts';
const now = Date.parse('2026-09-03T12:00:00Z');
test('theme preference round-trips, bounded and without trading content', () => {
  for (const theme of ['light','dark']) {
    const value = saveWorkspaceAppearance(theme,now);
    assert.equal(readWorkspaceAppearance(value,now),theme);
    assert.deepEqual(Object.keys(JSON.parse(value)),['version','theme','savedAt']);
    assert.ok(value.length < 256);
  }
});
test('invalid, future and expired appearance preferences are ignored', () => {
  for (const raw of [null,'{','null',' '.repeat(257),JSON.stringify({version:2,theme:'light',savedAt:now}),JSON.stringify({version:1,theme:'other',savedAt:now}),saveWorkspaceAppearance('light',now+1),saveWorkspaceAppearance('dark',now-APPEARANCE_RETENTION_MS-1)]) assert.equal(readWorkspaceAppearance(raw,now),null);
});
