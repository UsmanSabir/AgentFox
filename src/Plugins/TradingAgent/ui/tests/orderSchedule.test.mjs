import test from 'node:test';
import assert from 'node:assert/strict';
import { psxDateInput, scheduleDateError, describePsxDate } from '../src/orderSchedule.ts';

test('activation validation uses the Pakistan day across UTC midnight', () => {
  const now = new Date('2026-09-17T20:00:00Z');
  assert.equal(psxDateInput(now), '2026-09-18');
  assert.ok(scheduleDateError('2026-09-17', now));
  assert.equal(scheduleDateError('2026-09-18', now), null);
  assert.equal(scheduleDateError('2026-09-19', now), null); // Weekends may wait for the next open.
});

test('missing and impossible dates cannot create a schedule', () => {
  for (const value of ['', 'tomorrow', '2026-02-30', '2026-13-01'])
    assert.ok(scheduleDateError(value, new Date('2026-01-01T00:00:00Z')));
});

test('stored midnight PKT displays the intended date even in a western browser timezone', () => {
  const previous = process.env.TZ;
  try {
    process.env.TZ = 'America/Los_Angeles';
    assert.equal(describePsxDate('2026-09-17T19:00:00Z'), describePsxDate('2026-09-18T05:00:00Z'));
    assert.match(describePsxDate('2026-09-17T19:00:00Z'), /18.*PKT/);
  } finally {
    if (previous === undefined) delete process.env.TZ;
    else process.env.TZ = previous;
  }
});
