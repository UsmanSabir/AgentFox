import { test } from 'node:test';
import assert from 'node:assert/strict';
import { retainedWatchlistFocus, watchlistGridTarget, moveWatchlistRow } from '../src/watchlistNavigation.ts';

test('refresh/reorder preserve focus by symbol, not row number', () => {
  assert.equal(retainedWatchlistFocus(['C','B','A'], 'B', 'A'), 'B');
  assert.equal(retainedWatchlistFocus(['C','A'], 'B', 'A'), 'A');
  assert.equal(retainedWatchlistFocus(['C'], 'B', 'A'), 'C');
  assert.equal(retainedWatchlistFocus([], 'B', 'A'), null);
});
test('grid arrows and pages clamp at boundaries', () => {
  assert.deepEqual(watchlistGridTarget('ArrowUp',0,0,5,3),{row:0,column:0});
  assert.deepEqual(watchlistGridTarget('ArrowRight',2,2,5,3),{row:2,column:2});
  assert.deepEqual(watchlistGridTarget('PageDown',1,1,20,7),{row:8,column:1});
  assert.deepEqual(watchlistGridTarget('PageDown',18,1,20,7),{row:19,column:1});
  assert.deepEqual(watchlistGridTarget('PageUp',1,1,20,7),{row:0,column:1});
});
test('Home/End stay in a row; Ctrl+Home/End move through the grid', () => {
  assert.deepEqual(watchlistGridTarget('Home',3,2,8,4),{row:3,column:0});
  assert.deepEqual(watchlistGridTarget('End',3,0,8,4),{row:3,column:2});
  assert.deepEqual(watchlistGridTarget('Home',3,2,8,4,true),{row:0,column:0});
  assert.deepEqual(watchlistGridTarget('End',3,0,8,4,true),{row:7,column:2});
  assert.equal(watchlistGridTarget('Enter',3,0,8,4),null);
  assert.equal(watchlistGridTarget('ArrowDown',0,0,0,4),null);
});
test('keyboard reorder is immutable and stays within pinned/regular lanes', () => {
  const rows = [{symbol:'A',pinned:true},{symbol:'B',pinned:true},{symbol:'C',pinned:false},{symbol:'D',pinned:false}];
  assert.deepEqual(moveWatchlistRow(rows,'B',-1)?.map(e=>e.symbol),['B','A','C','D']);
  assert.deepEqual(moveWatchlistRow(rows,'C',1)?.map(e=>e.symbol),['A','B','D','C']);
  assert.deepEqual(rows.map(e=>e.symbol),['A','B','C','D']);
  assert.equal(moveWatchlistRow(rows,'B',1),null);
  assert.equal(moveWatchlistRow(rows,'C',-1),null);
  assert.equal(moveWatchlistRow(rows,'A',-1),null);
  assert.equal(moveWatchlistRow(rows,'D',1),null);
  assert.equal(moveWatchlistRow(rows,'missing',1),null);
});
