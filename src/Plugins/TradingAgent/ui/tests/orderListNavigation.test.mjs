import { test } from 'node:test';
import assert from 'node:assert/strict';
import { orderRowIndex } from '../src/orderListNavigation.ts';
test('order row navigation is bounded to visible rows', () => {
  assert.equal(orderRowIndex('ArrowDown',0,3),1);
  assert.equal(orderRowIndex('ArrowDown',2,3),2);
  assert.equal(orderRowIndex('ArrowUp',1,3),0);
  assert.equal(orderRowIndex('ArrowUp',0,3),0);
  assert.equal(orderRowIndex('Home',2,3),0);
  assert.equal(orderRowIndex('End',0,3),2);
  assert.equal(orderRowIndex('ArrowDown',-1,3),null);
  assert.equal(orderRowIndex('End',0,0),null);
});
test('navigation does not interpret action or selection keys', () => {
  for (const key of ['Enter',' ','Delete','Backspace','Escape','c','s','b','Tab']) assert.equal(orderRowIndex(key,0,3),null);
});
