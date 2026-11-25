import test from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import { newOrderMenuItems, registerNewOrderMenuItem } from '../src/newOrderMenuExtensions.ts';

test('an edition adds, replaces by id and removes its own drop-down entries', () => {
  assert.deepEqual(get(newOrderMenuItems), [], 'a community build shows only the quick orders');
  const seen = [];
  const first = registerNewOrderMenuItem({ id: 'rules', label: 'Rule orders', run: c => seen.push(c.selectedSymbol) });
  const replaced = registerNewOrderMenuItem({ id: 'rules', label: 'Rule orders…', run: c => seen.push(`2:${c.selectedSymbol}`) });
  assert.deepEqual(get(newOrderMenuItems).map(i => i.label), ['Rule orders…']);
  get(newOrderMenuItems)[0].run({ selectedSymbol: 'SYS' });
  assert.deepEqual(seen, ['2:SYS']);
  first();
  assert.equal(get(newOrderMenuItems).length, 1, 'removing a replaced entry leaves its replacement');
  replaced();
  assert.deepEqual(get(newOrderMenuItems), []);
});
