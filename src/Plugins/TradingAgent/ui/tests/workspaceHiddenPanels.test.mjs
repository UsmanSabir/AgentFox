import test from 'node:test';
import assert from 'node:assert/strict';
import { visiblePanels, presetWithoutHidden, knownPanelIds, adoptablePanelIds, workspacePresetPanels } from '../src/workspaceComposition.ts';
import { readWorkspaceLayout, saveWorkspaceLayout, layoutFitsPanels, dropPanelsFromTrays } from '../src/workspaceLayout.ts';

// A panel can be hidden WITHOUT leaving the catalogue (DockWorkspace's `hiddenPanelIds`). These are the pure rules the component applies.

const catalogue = ['list', 'chart', 'extra', 'logs'].map(id => ({ id, title: id, region: 'center', description: id }));
const preset = { id: 'trading', label: 'Trading', active: { left: 'list', center: 'extra' }, offersUnplaced: true,
  columns: [{ panels: ['list'], active: 'list' }, { panels: ['chart', 'extra'], active: 'extra', weight: 2 }],
  autoHide: { bottom: { panels: ['extra', 'logs'], active: 'extra', size: 280 } } };

const now = Date.parse('2026-09-03T12:00:00Z');
const dockLayout = () => ({
  grid: { root: { type: 'branch', data: [], size: 100 }, width: 1200, height: 800, orientation: 'HORIZONTAL' },
  panels: {
    chart: { id: 'chart', contentComponent: 'chart', title: 'Chart' },
    extra: { id: 'extra', contentComponent: 'extra', title: 'Extra' }
  }
});
const savedWithExtra = () => saveWorkspaceLayout('premium', 'trading', dockLayout(), now,
  { bottom: { ids: ['logs'], active: 'logs', size: 280 } }, undefined, ['chart', 'extra', 'logs']);

test('not offered: Find panel and focus requests see the catalogue without the hidden panel, and the catalogue keeps it', () => {
  const hidden = new Set(['extra']);
  assert.deepEqual(visiblePanels(workspacePresetPanels(catalogue, preset), hidden).map(p => p.id), ['list', 'chart', 'logs']);
  assert.equal(visiblePanels(catalogue, new Set()), catalogue, 'nothing hidden: the same list');
  assert.equal(catalogue.length, 4, 'the catalogue itself is not changed');
  // A focused view that does not place the panel still never offers it.
  const focused = { ...preset, offersUnplaced: false };
  assert.deepEqual(visiblePanels(workspacePresetPanels(catalogue, focused), hidden).map(p => p.id), ['list', 'chart', 'logs']);
});

test('default build: a hidden panel is skipped, and a group left without its active panel starts on what remains', () => {
  const built = presetWithoutHidden(preset, new Set(['extra']));
  assert.deepEqual(built.columns.map(c => c.panels), [['list'], ['chart']]);
  assert.deepEqual(built.columns.map(c => c.active), ['list', 'chart']);
  assert.equal(built.columns[1].weight, 2, 'geometry is kept');
  assert.deepEqual(built.autoHide.bottom, { panels: ['logs'], active: 'logs', size: 280 });
  assert.deepEqual(built.active, { left: 'list' }, 'a view never opens on a hidden panel');
  const alone = presetWithoutHidden({ ...preset, columns: [{ panels: ['list'], active: 'list' }, { panels: ['extra'], active: 'extra' }],
    autoHide: { bottom: { panels: ['extra'], active: 'extra' } } }, new Set(['extra']));
  assert.deepEqual(alone.columns.map(c => c.panels), [['list']], 'a column left empty is dropped');
  assert.deepEqual(alone.autoHide, {}, 'and so is a tray');
  assert.equal(presetWithoutHidden(preset, new Set()), preset);
  assert.deepEqual(preset.columns[1].panels, ['chart', 'extra'], 'the preset is not changed');
});

test('a saved layout that holds a hidden panel is kept, not reset: the id is still in the catalogue', () => {
  const record = savedWithExtra();
  const ids = catalogue.map(p => p.id);
  assert.deepEqual(readWorkspaceLayout(JSON.stringify(record), 'premium', ids, ['trading'], now), record);
  assert.equal(readWorkspaceLayout(JSON.stringify(record), 'premium', ids.filter(id => id !== 'extra'), ['trading'], now), null,
    'taking the panel out of the catalogue is what resets a layout: the reason hiding exists');
  // The view-level check the dock makes before it keeps a restored layout.
  const allowed = new Set(ids);
  assert.equal(layoutFitsPanels(record.layout.panels, record.autoHide, allowed), true);
  assert.equal(layoutFitsPanels(record.layout.panels, record.autoHide, new Set(['chart', 'logs'])), false);
  assert.equal(layoutFitsPanels(record.layout.panels, { bottom: { ids: ['nowhere'], active: 'nowhere', size: 280 } }, allowed), false, 'an unknown id still fails');
});

test('a hidden panel is not recorded as known, so un-hiding it adopts it by the existing rule', () => {
  assert.deepEqual(knownPanelIds(catalogue, new Set(['extra'])), ['list', 'chart', 'logs']);
  assert.deepEqual(knownPanelIds(catalogue, new Set()), ['list', 'chart', 'extra', 'logs']);
  const offered = ['list', 'chart', 'extra', 'logs'];
  const placed = new Set(['list', 'chart', 'logs']);
  const seenWhileHidden = new Set(knownPanelIds(catalogue, new Set(['extra'])));
  assert.deepEqual(adoptablePanelIds(offered, seenWhileHidden, new Set(['extra']), id => placed.has(id)), [], 'still hidden: not adopted');
  assert.deepEqual(adoptablePanelIds(offered, seenWhileHidden, new Set(), id => placed.has(id)), ['extra'], 'un-hidden: adopted as new');
  assert.deepEqual(adoptablePanelIds(offered, new Set(), new Set(['extra']), id => placed.has(id)), [], 'never adopted while hidden, whatever the record knows');
  assert.deepEqual(adoptablePanelIds(offered, new Set(offered), new Set(), () => false), [], 'a panel the record has seen is not adopted');
  // A record written while visible knows the panel: hiding and un-hiding in one session is handled live, not by `known`.
  assert.ok(savedWithExtra().known.includes('extra'));
});

test('hiding a panel that is open takes it out of the trays too, without touching the others', () => {
  const trays = { bottom: { ids: ['ledger', 'extra'], active: 'extra', size: 280 }, left: { ids: ['extra'], active: 'extra', size: 300 }, right: { ids: ['plan'], active: 'plan', size: 300 } };
  const result = dropPanelsFromTrays(trays, new Set(['extra']), 'bottom');
  assert.equal(result.changed, true);
  assert.equal(result.closesOpenEdge, true, 'the peeking tray showed it, so the peek closes first');
  assert.deepEqual(result.trays.bottom, { ids: ['ledger'], active: 'ledger', size: 280 });
  assert.equal(result.trays.left, undefined, 'a tray left empty goes');
  assert.equal(result.trays.right, trays.right, 'an untouched tray is the same object');
  assert.equal(trays.bottom.ids.length, 2, 'the input is not changed');
  assert.equal(dropPanelsFromTrays(trays, new Set(['absent'])).changed, false);
  assert.equal(dropPanelsFromTrays(trays, new Set(['extra']), 'right').closesOpenEdge, false, 'a different tray was peeking');
});
