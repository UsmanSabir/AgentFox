import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import { orderTypeExtensions, registerOrderTypeExtension, extensionForTriggerKind } from '../src/orderTypeExtensions.ts';

test('nothing is registered in a community build', () => {
  assert.deepEqual(get(orderTypeExtensions), []);
  assert.equal(extensionForTriggerKind([], 'Rule'), null);
});

test('a registered extension is found by its trigger kind and removed by its own handle', () => {
  const editor = {};
  const remove = registerOrderTypeExtension({ id: 'rules', label: 'Rules', triggerKind: 'Rule', editor });
  assert.equal(get(orderTypeExtensions).length, 1);
  assert.equal(extensionForTriggerKind(get(orderTypeExtensions), 'Rule').editor, editor);
  assert.equal(extensionForTriggerKind(get(orderTypeExtensions), 'PriceBelow'), null);
  assert.equal(extensionForTriggerKind(get(orderTypeExtensions), null), null);
  remove();
  assert.deepEqual(get(orderTypeExtensions), []);
});

test('registering the same id again replaces rather than duplicates the tile', () => {
  const first = registerOrderTypeExtension({ id: 'rules', label: 'Rules', triggerKind: 'Rule', editor: {} });
  const second = registerOrderTypeExtension({ id: 'rules', label: 'Rules v2', triggerKind: 'Rule', editor: {} });
  assert.equal(get(orderTypeExtensions).length, 1);
  assert.equal(get(orderTypeExtensions)[0].label, 'Rules v2');
  first();
  assert.equal(get(orderTypeExtensions).length, 1, 'an older handle must not remove its replacement');
  second();
  assert.deepEqual(get(orderTypeExtensions), []);
});
