import test from 'node:test';
import assert from 'node:assert/strict';
import { pluginSidebarRequest } from '../src/lib/pluginLayout.ts';

test('only the active same-origin frame can request a temporary sidebar layout', () => {
  const frame = {};
  const origin = 'https://agentfox.test';
  const message = { type:'agentfox:layout', sidebar:'collapsed' };
  assert.equal(pluginSidebarRequest(origin, origin, frame, frame, message), 'collapsed');
  assert.equal(pluginSidebarRequest(origin, origin, frame, frame, { ...message, sidebar:'default' }), 'default');
  assert.equal(pluginSidebarRequest('https://other.test', origin, frame, frame, message), null);
  assert.equal(pluginSidebarRequest(origin, origin, {}, frame, message), null);
  assert.equal(pluginSidebarRequest(origin, origin, null, null, message), null);
  for (const data of [null, 'collapsed', {}, { ...message, sidebar:true }, { ...message, type:'other' }]) {
    assert.equal(pluginSidebarRequest(origin, origin, frame, frame, data), null);
  }
});
