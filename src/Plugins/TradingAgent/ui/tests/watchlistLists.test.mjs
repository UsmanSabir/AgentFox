import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  addWatchlistList, changeWatchlistMembership, readWatchlistLists,
  watchlistListsKey, watchlistSectors, writeWatchlistLists
} from '../src/watchlistLists.ts';

test('sector groups use the watched tickers with known sector metadata', () => {
  const groups = watchlistSectors([
    { symbol: 'ATRL', sectorName: 'Refinery' },
    { symbol: 'NRL', sectorName: ' refinery ' },
    { symbol: 'LUCK', sectorName: 'Cement' },
    { symbol: 'OTHER' }
  ]);
  assert.deepEqual(groups.map(group => [group.name, [...group.symbols]]), [
    ['Cement', ['LUCK']], ['Refinery', ['ATRL', 'NRL']]
  ]);
});

test('editing one stock can change several lists while preserving other changes', () => {
  const lists = [
    { name: 'First', symbols: ['ATRL', 'PRL'] },
    { name: 'Second', symbols: ['MARI'] },
    { name: 'Third', symbols: ['ATRL'] }
  ];
  assert.deepEqual(changeWatchlistMembership(lists, 'ATRL', ['First'], ['Second']), [
    { name: 'First', symbols: ['PRL'] },
    { name: 'Second', symbols: ['MARI', 'ATRL'] },
    { name: 'Third', symbols: ['ATRL'] }
  ]);
  assert.throws(() => addWatchlistList(lists, ' FIRST '), /already have a list/);
});

test('lists round trip and are swept when expired or malformed', () => {
  const values = new Map();
  const storage = {
    getItem: key => values.get(key),
    setItem: (key, raw) => values.set(key, raw),
    removeItem: key => values.delete(key)
  };
  const lists = [{ name: 'Keep an eye on', symbols: ['PRL'] }];
  const now = 10_000;
  writeWatchlistLists(storage, lists, now);
  assert.deepEqual(readWatchlistLists(storage, now + 1), lists);
  assert.deepEqual(readWatchlistLists(storage, now + 180 * 86_400_000), []);
  assert.equal(values.has(watchlistListsKey), false);
  values.set(watchlistListsKey, '{');
  assert.deepEqual(readWatchlistLists(storage, now), []);
  assert.equal(values.has(watchlistListsKey), false);
});
