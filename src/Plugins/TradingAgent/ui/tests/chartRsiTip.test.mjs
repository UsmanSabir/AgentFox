import { test } from 'node:test';
import assert from 'node:assert/strict';
import { rsiTipPosition, rsiZone } from '../src/chartRsiTip.ts';

test('a value names its band at and beyond the analyzer thresholds, and none between', () => {
  assert.equal(rsiZone(70, 35, 70), 'overbought');
  assert.equal(rsiZone(81.2, 35, 70), 'overbought');
  assert.equal(rsiZone(35, 35, 70), 'oversold');
  assert.equal(rsiZone(22, 35, 70), 'oversold');
  assert.equal(rsiZone(56.41, 35, 70), null);
  // The thresholds are the response's, not a fixed 30/70.
  assert.equal(rsiZone(32, 30, 70), null);
});

const pane = { top: 300, height: 90, width: 800 };

test('the tip sits right of and above the line', () => {
  assert.deepEqual(rsiTipPosition({ x: 200, y: 360 }, pane), { left: 212, top: 324 });
});

test('the tip flips left of the line near the plot right edge', () => {
  assert.deepEqual(rsiTipPosition({ x: 700, y: 360 }, pane), { left: 538, top: 324 });
});

test('the tip drops below the line rather than crossing into the price pane', () => {
  assert.deepEqual(rsiTipPosition({ x: 200, y: 310 }, pane), { left: 212, top: 322 });
});

test('the tip never leaves the pane or the plot', () => {
  // A line at the very foot of a short pane: below would overrun, so it is held inside.
  assert.equal(rsiTipPosition({ x: 200, y: 312 }, { top: 300, height: 40, width: 800 }).top, 316);
  // A plot too narrow for either side keeps the tip at its left edge.
  assert.equal(rsiTipPosition({ x: 60, y: 360 }, { top: 300, height: 90, width: 180 }).left, 0);
});
