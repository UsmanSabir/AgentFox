import test from 'node:test';
import assert from 'node:assert/strict';
import { searchWorkspaceCommands } from '../src/workspaceSearch.ts';

const commands = [
  { id:'panel.portfolio', label:'Show Portfolio & working orders', keywords:'Private balances, holdings and broker book', run() {} },
  { id:'panel.logs', label:'Show Order logs', keywords:'Live broker fills and cancellations', run() {} },
  { id:'layout.reset', label:'Reset view to default', run() {} }
];

test('panel search matches words across names and descriptions regardless of case, order or spacing', () => {
  assert.deepEqual(searchWorkspaceCommands(commands, '  BROKER   orders ').map(c => c.id), ['panel.portfolio']);
  assert.deepEqual(searchWorkspaceCommands(commands, 'fills logs').map(c => c.id), ['panel.logs']);
  assert.deepEqual(searchWorkspaceCommands(commands, 'hold').map(c => c.id), ['panel.portfolio']);
});

test('blank queries retain catalogue order and commands without keywords remain searchable', () => {
  assert.deepEqual(searchWorkspaceCommands(commands, ' \n '), commands);
  assert.deepEqual(searchWorkspaceCommands(commands, 'default reset').map(c => c.id), ['layout.reset']);
  assert.deepEqual(searchWorkspaceCommands(commands, 'broker').map(c => c.id), ['panel.portfolio', 'panel.logs']);
  assert.deepEqual(commands.map(c => c.id), ['panel.portfolio', 'panel.logs', 'layout.reset']);
});

test('every search word must match and a missing result is empty', () => {
  assert.deepEqual(searchWorkspaceCommands(commands, 'broker reset'), []);
  assert.deepEqual(searchWorkspaceCommands(commands, 'nonexistent'), []);
});
