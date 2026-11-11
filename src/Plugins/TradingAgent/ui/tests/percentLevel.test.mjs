import test from 'node:test';
import assert from 'node:assert/strict';
import { percentLevel } from '../src/percentLevel.ts';

// Each expected value is what the server's decimal arithmetic produces:
// Math.Round(reference * (1 -/+ Math.Round(percent, 2, AwayFromZero) / 100), 2, AwayFromZero).

test('ordinary levels agree with the server', () => {
  assert.equal(percentLevel('PercentDrop', 100, 3), 97);
  assert.equal(percentLevel('PercentRise', 100, 3), 103);
  assert.equal(percentLevel('PercentDrop', 448.75, 1), 444.26);
  assert.equal(percentLevel('PercentDrop', 77.69, 3), 75.36);
});

test('the percent is rounded to two places first, half away from zero, as the arm endpoint does', () => {
  // 2.555% is armed as 2.56%: 100 x 0.9744 = 97.44. Unrounded it would be 97.445 -> 97.45 here.
  assert.equal(percentLevel('PercentDrop', 100, 2.555), 97.44);
  // 1.005% is armed as 1.01% (binary 1.005 is 1.00499...): 200 x 1.0101 = 202.02.
  assert.equal(percentLevel('PercentRise', 200, 1.005), 202.02);
});

test('a product on a half paisa rounds away from zero, where binary arithmetic rounded it down', () => {
  // Each of these the old floating-point version showed one paisa LOWER than the server arms.
  assert.equal(percentLevel('PercentDrop', 1.3, 5), 1.24, '1.3 x 0.95 = 1.235 (old: 1.23)');
  assert.equal(percentLevel('PercentRise', 1.25, 2), 1.28, '1.25 x 1.02 = 1.275 (old: 1.27)');
  assert.equal(percentLevel('PercentRise', 1, 0.5), 1.01, '1 x 1.005 = 1.005 (old: 1.00)');
  assert.equal(percentLevel('PercentRise', 1.4, 2.5), 1.44, '1.4 x 1.025 = 1.435 (old: 1.43)');
});

test('products that are not on a half paisa are unchanged', () => {
  assert.equal(percentLevel('PercentDrop', 10.05, 5), 9.55);
  assert.equal(percentLevel('PercentRise', 1.15, 1.5), 1.17);
  assert.equal(percentLevel('PercentDrop', 4.35, 1), 4.31);
});

test('a percent that rounds to zero, or anything out of range, produces no level', () => {
  assert.equal(percentLevel('PercentDrop', 100, 0.004), null);
  assert.equal(percentLevel('PercentDrop', 100, 0), null);
  assert.equal(percentLevel('PercentDrop', 100, 51), null);
  assert.equal(percentLevel('PercentDrop', 0, 3), null);
  assert.equal(percentLevel('PriceBelow', 100, 3), null);
});
