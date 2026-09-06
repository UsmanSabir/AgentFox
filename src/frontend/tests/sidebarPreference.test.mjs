import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSidebarCollapsed, serializeSidebarCollapsed } from '../src/lib/sidebarPreference.ts';

test('sidebar collapse preference round-trips both explicit states', () => {
  assert.equal(parseSidebarCollapsed(serializeSidebarCollapsed(true)),true);
  assert.equal(parseSidebarCollapsed(serializeSidebarCollapsed(false)),false);
});

test('missing or malformed sidebar preferences fall back to responsive defaults', () => {
  for (const value of [null,'','1','TRUE','collapsed','{}'])
    assert.equal(parseSidebarCollapsed(value),null);
});
