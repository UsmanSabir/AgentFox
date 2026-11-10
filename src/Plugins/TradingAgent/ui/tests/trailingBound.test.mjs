import test from 'node:test';
import assert from 'node:assert/strict';
import { trailBoundBlock } from '../src/trailingBound.ts';

test('a minimum above a trailing sell trigger is reported with the high it needs (PRL, 2026-11-10)', () => {
  // 3% trail from 77.69 -> trigger 75.36; minimum 82 needs a high of 82 / 0.97 = 84.536 -> 84.54.
  const block = trailBoundBlock('SELL', 75.36, 82, 3);
  assert.deepEqual(block, { trigger: 75.36, bound: 82, neededExtreme: 84.54 });
});

test('the LUCK case: a minimum of 545 against a 1% trail needs a high of 550.51', () => {
  assert.equal(trailBoundBlock('SELL', 444.26, 545, 1).neededExtreme, 550.51);
});

test('a minimum at or below the trigger does not block — the bound is inclusive, as on the server', () => {
  assert.equal(trailBoundBlock('SELL', 75.36, 75.36, 3), null);
  assert.equal(trailBoundBlock('SELL', 75.36, 70, 3), null);
});

test('a trailing buy mirrors it: a maximum below the trigger needs a low', () => {
  // Trigger 103 (3% above a low of 100); maximum 101 needs a low of 101 / 1.03 = 98.058 -> 98.05.
  assert.deepEqual(trailBoundBlock('BUY', 103, 101, 3), { trigger: 103, bound: 101, neededExtreme: 98.05 });
  assert.equal(trailBoundBlock('BUY', 103, 103, 3), null);
  assert.equal(trailBoundBlock('BUY', 103, 110, 3), null);
});

test('no bound, no trigger or no trail percent says nothing', () => {
  assert.equal(trailBoundBlock('SELL', 75.36, null, 3), null);
  assert.equal(trailBoundBlock('SELL', null, 82, 3), null);
  assert.equal(trailBoundBlock('SELL', 75.36, 82, null), null);
  assert.equal(trailBoundBlock('SELL', 75.36, 0, 3), null);
});
