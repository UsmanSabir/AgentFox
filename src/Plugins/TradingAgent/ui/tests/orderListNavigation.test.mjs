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

test('a mouse click on a row symbol selects the stock; Enter or Space still only enters the row', async () => {
  const { isPointerClick } = await import('../src/orderListNavigation.ts');
  assert.equal(isPointerClick({ detail: 1 }), true);
  assert.equal(isPointerClick({ detail: 2 }), true, 'a double click is still a pointer');
  assert.equal(isPointerClick({ detail: 0 }), false, 'a keyboard-activated click has detail 0');
});

test('the Buy / Sell filter matches the order side, and All matches everything', async () => {
  const { matchesSide } = await import('../src/orderListNavigation.ts');
  for (const action of ['BUY', 'SELL', 'buy', null]) assert.equal(matchesSide(action, 'all'), true);
  assert.equal(matchesSide('BUY', 'BUY'), true);
  assert.equal(matchesSide('buy', 'BUY'), true);
  assert.equal(matchesSide('SELL', 'BUY'), false);
  assert.equal(matchesSide('SELL', 'SELL'), true);
  assert.equal(matchesSide(null, 'SELL'), false, 'an order with no side is not guessed onto one');
});
