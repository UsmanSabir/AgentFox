import test from 'node:test';
import assert from 'node:assert/strict';
import { amountInWords } from '../src/amountInWords.ts';

test('extra zeroes produce clearly different amount readbacks', () => {
  assert.equal(amountInWords(10000), 'Ten thousand rupees');
  assert.equal(amountInWords(100000), 'One hundred thousand rupees');
  assert.equal(amountInWords(1000000), 'One million rupees');
  assert.equal(amountInWords(1234567), 'One million two hundred thirty-four thousand five hundred sixty-seven rupees');
});

test('currency readbacks handle paisa and rounding across a rupee boundary', () => {
  assert.equal(amountInWords(0), 'Zero rupees');
  assert.equal(amountInWords(1), 'One rupee');
  assert.equal(amountInWords(10000.25), 'Ten thousand rupees and twenty-five paisa');
  assert.equal(amountInWords(0.01), 'Zero rupees and one paisa');
  assert.equal(amountInWords(999.999), 'One thousand rupees');
});

test('missing, invalid and unsafe amounts never display a misleading readback', () => {
  for (const value of [null, undefined, NaN, Infinity, -1, Number.MAX_SAFE_INTEGER])
    assert.equal(amountInWords(value), '');
});
