import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readWorkspaceLayout, saveWorkspaceLayout, WORKSPACE_RETENTION_MS, MAX_LAYOUT_LENGTH } from '../src/workspaceLayout.ts';

const now = Date.parse('2026-09-03T12:00:00Z');
const layout = () => ({
  grid: { root: { type: 'branch', data: [], size: 100 }, width: 1200, height: 800, orientation: 'HORIZONTAL' },
  panels: { chart: { id: 'chart', contentComponent: 'chart', title: 'Price chart' } }
});
const saved = () => saveWorkspaceLayout('premium', 'trading', layout(), now);
const read = (value) => readWorkspaceLayout(JSON.stringify(value), 'premium', ['chart', 'watchlist'], ['trading'], now);

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

test('auto-hidden view restores only known unique panels outside the grid and a bounded height', () => {
  const tray = {ids:['watchlist'],active:'watchlist',height:250};
  const value = saveWorkspaceLayout('premium','trading',layout(),now,tray);
  assert.deepEqual(read(value).bottomTray,tray);
  tray.ids.push('chart');
  assert.deepEqual(value.bottomTray.ids,['watchlist']);
  for (const bottomTray of [
    null, false, 0,
    {ids:[],active:'watchlist',height:250}, {ids:['chart'],active:'chart',height:250},
    {ids:['watchlist','watchlist'],active:'watchlist',height:250},
    {ids:['watchlist'],active:'unknown',height:250},
    {ids:['unknown'],active:'unknown',height:250},
    {ids:['watchlist'],active:'watchlist',height:Infinity},
    {ids:['watchlist'],active:'watchlist',height:10}
  ]) assert.equal(read({...value,bottomTray}),null);
});
