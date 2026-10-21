import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readWorkspaceLayout, saveWorkspaceLayout, resolveWorkspacePreset, WORKSPACE_RETENTION_MS, MAX_LAYOUT_LENGTH } from '../src/workspaceLayout.ts';

const now = Date.parse('2026-09-03T12:00:00Z');
const layout = () => ({
  grid: { root: { type: 'branch', data: [], size: 100 }, width: 1200, height: 800, orientation: 'HORIZONTAL' },
  panels: { chart: { id: 'chart', contentComponent: 'chart', title: 'Price chart' } }
});
const saved = () => saveWorkspaceLayout('premium', 'trading', layout(), now);
const read = (value) => readWorkspaceLayout(JSON.stringify(value), 'premium', ['chart', 'watchlist'], ['trading'], now);

test('explicit compact and expanded widths survive serialization, including an unplaced watchlist', () => {
  for (const width of [180, 395]) {
    const widths = {watchlist:width};
    const value = saveWorkspaceLayout('premium','trading',layout(),now,undefined,undefined,undefined,widths);
    assert.deepEqual(read(value).panelWidths, {watchlist:width});
    widths.watchlist = 600;
    assert.equal(value.panelWidths.watchlist, width, 'saved preferences are independent of live state');
  }
  assert.equal(read(saved()).panelWidths, undefined, 'older layouts still restore');
  for (const panelWidths of [null, [], {unknown:180}, {watchlist:179}, {watchlist:100001},
    {watchlist:'180'}, {watchlist:null}]) assert.equal(read({...saved(),panelWidths}), null);
});

test('maximized panel restores only a panel still in the docked grid', () => {
  const value = saveWorkspaceLayout('premium','trading',layout(),now,undefined,'chart');
  assert.equal(read(value).maximized,'chart');
  for (const maximized of [null,5,'missing','watchlist']) assert.equal(read({...value,maximized}),null);
  assert.equal(saveWorkspaceLayout('premium','trading',layout(),now,undefined,'watchlist').maximized,undefined);
  assert.equal(read(saved()).maximized,undefined);
});

test('accepts current edition view metadata, including a hidden catalogue panel', () => {
  assert.deepEqual(read(saved()), saved());
});

test('rejects malformed, incompatible and expired preferences', () => {
  assert.equal(readWorkspaceLayout('{', 'premium', ['chart'], ['trading'], now), null);
  for (const change of [{version:2}, {edition:'public'}, {preset:'removed'}, {savedAt:now + 1}, {savedAt:now - WORKSPACE_RETENTION_MS - 1}, {savedAt:null}]) {
    assert.equal(read({...saved(), ...change}), null);
  }
});

test('rejects unregistered components, mismatched ids, and component parameters', () => {
  for (const panel of [
    {id:'chart', contentComponent:'unknown'},
    {id:'other', contentComponent:'chart'},
    {id:'chart', contentComponent:'chart', params:{orderDraft:'must never restore'}}
  ]) {
    const value = saved();
    value.layout.panels.chart = panel;
    assert.equal(read(value), null);
  }
  const value = saved();
  value.layout.panels.unknown = {id:'unknown', contentComponent:'unknown'};
  assert.equal(read(value), null);
});

test('serialization strips component parameters without mutating live layout', () => {
  const input = layout();
  input.panels.chart.params = {orderDraft:{symbol:'MARI', quantity:10}};
  const value = saveWorkspaceLayout('premium', 'trading', input, now);
  assert.equal('params' in value.layout.panels.chart, false);
  assert.equal(input.panels.chart.params.orderDraft.quantity, 10);
  assert.ok(read(value));
});

test('rejects floating, pop-out and excessively large saved views', () => {
  for (const key of ['floatingGroups','popoutGroups']) {
    const value = saved();
    value.layout[key] = [{}];
    assert.equal(read(value), null);
  }
  assert.equal(readWorkspaceLayout(' '.repeat(MAX_LAYOUT_LENGTH + 1), 'premium', ['chart'], ['trading'], now), null);
});

test('auto-hidden edges restore only known unique panels outside the grid and bounded sizes', () => {
  const autoHide = {left:{ids:['watchlist'],active:'watchlist',size:250}};
  const value = saveWorkspaceLayout('premium','trading',layout(),now,autoHide);
  assert.deepEqual(read(value).autoHide,autoHide);
  autoHide.left.ids.push('chart');
  assert.deepEqual(value.autoHide.left.ids,['watchlist']);
  for (const tray of [
    null, false, 0,
    {ids:[],active:'watchlist',size:250}, {ids:['chart'],active:'chart',size:250},
    {ids:['watchlist','watchlist'],active:'watchlist',size:250},
    {ids:['watchlist'],active:'unknown',size:250},
    {ids:['unknown'],active:'unknown',size:250},
    {ids:['watchlist'],active:'watchlist',size:Infinity},
    {ids:['watchlist'],active:'watchlist',size:10}
  ]) assert.equal(read({...value,autoHide:{left:tray}}),null);
  assert.equal(read({...value,autoHide:{top:{ids:['watchlist'],active:'watchlist',size:250}}}),null);
});

test('auto-hidden edges cannot overlap one another', () => {
  const value = saved();
  value.autoHide = {
    left:{ids:['watchlist'],active:'watchlist',size:250},
    right:{ids:['watchlist'],active:'watchlist',size:300}
  };
  assert.equal(read(value),null);
});

test('the retired bottom tray shape migrates to the edge-neutral shape', () => {
  const value = saved();
  value.bottomTray = {ids:['watchlist'],active:'watchlist',height:250};
  assert.deepEqual(read(value).autoHide,{bottom:{ids:['watchlist'],active:'watchlist',size:250}});
  assert.equal(read(value).bottomTray,undefined);
});

test('My view is kept beside the working layout, never in its place', async () => {
  const { myViewStorageKey } = await import('../src/workspaceLayout.ts');
  assert.equal(myViewStorageKey('trading.premium-workstation.layout.v1'), 'trading.premium-workstation.layout.v1.my-view');
  assert.notEqual(myViewStorageKey('k'), 'k');
});

test('My view is read with the same rules as the working layout, and a restore renews its 180 days', () => {
  const view = saved();
  assert.deepEqual(read(view)?.layout.panels, layout().panels);
  const lapsed = { ...view, savedAt: now - WORKSPACE_RETENTION_MS - 1 };
  assert.equal(read(lapsed), null, 'unused for 180 days, it lapses like the working layout');
  const renewed = { ...lapsed, savedAt: now };
  assert.ok(read(renewed), 'each restore writes it back with a fresh savedAt');
});

test('renamed presets retain saved geometry, drawers and age for working layouts and My view', () => {
  const aliases = {market:'trading',review:'portfolio','trade-hub':'copy-trading'};
  const ids = ['trading','portfolio','copy-trading'];
  for (const [oldId,newId] of Object.entries(aliases)) {
    const value = saveWorkspaceLayout('premium',oldId,layout(),now - 1000,
      {bottom:{ids:['watchlist'],active:'watchlist',size:282}},'chart',['chart','watchlist']);
    const restored = readWorkspaceLayout(JSON.stringify(value),'premium',['chart','watchlist'],ids,now,aliases);
    assert.deepEqual(restored,{...value,preset:newId});
    assert.equal(resolveWorkspacePreset(oldId,ids,aliases),newId,'deep links follow the same alias');
    assert.equal(readWorkspaceLayout(JSON.stringify({...value,savedAt:now-WORKSPACE_RETENTION_MS-1}),
      'premium',['chart','watchlist'],ids,now,aliases),null,'a rename must not renew expired layouts');
  }
});

test('preset aliases cannot revive unregistered views or weaken saved-layout validation', () => {
  assert.equal(resolveWorkspacePreset('old',['trading'],{old:'missing'}),null);
  assert.equal(resolveWorkspacePreset('toString',['trading']),null);
  assert.equal(resolveWorkspacePreset('trading',['trading'],{trading:'missing'}),'trading');
  assert.equal(resolveWorkspacePreset(null,['trading']),null);
  const invalid = {...saved(),preset:'old',edition:'other'};
  assert.equal(readWorkspaceLayout(JSON.stringify(invalid),'premium',['chart'],['trading'],now,{old:'trading'}),null);
});
