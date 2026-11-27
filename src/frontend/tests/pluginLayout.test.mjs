import test from 'node:test';
import assert from 'node:assert/strict';
import { pluginSidebarRequest, pluginTitleRequest, pluginPageTitle, PLUGIN_TITLE_PREFIX_MAX } from '../src/lib/pluginLayout.ts';

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

test('only the active same-origin frame can lead the tab title, with a short plain prefix', () => {
  const frame = {};
  const origin = 'https://agentfox.test';
  const message = { type:'agentfox:title', prefix:' 168,432 ▲0.45% KSE ' };
  assert.equal(pluginTitleRequest(origin, origin, frame, frame, message), '168,432 ▲0.45% KSE');
  assert.equal(pluginTitleRequest(origin, origin, frame, frame, { ...message, prefix:null }), null);
  assert.equal(pluginTitleRequest(origin, origin, frame, frame, { ...message, prefix:'  ' }), null);
  assert.equal(pluginTitleRequest('https://other.test', origin, frame, frame, message), undefined);
  assert.equal(pluginTitleRequest(origin, origin, {}, frame, message), undefined);
  assert.equal(pluginTitleRequest(origin, origin, null, null, message), undefined);
  for (const data of [null, 'x', {}, { ...message, prefix:7 }, { ...message, type:'agentfox:layout' },
    { ...message, prefix:'a\nb' }, { ...message, prefix:'x'.repeat(PLUGIN_TITLE_PREFIX_MAX + 1) }]) {
    assert.equal(pluginTitleRequest(origin, origin, frame, frame, data), undefined);
  }
});

test('the page title is led by the prefix only when one is set', () => {
  assert.equal(pluginPageTitle('Trading', '168,432 KSE'), '168,432 KSE · Trading · AgentFox');
  assert.equal(pluginPageTitle('Trading', null), 'Trading · AgentFox');
  assert.equal(pluginPageTitle(undefined, null), 'Plugin · AgentFox');
});
