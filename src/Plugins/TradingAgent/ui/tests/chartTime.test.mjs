import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TickMarkType } from 'lightweight-charts';
import { chartTimeFormatters } from '../src/chartTime.ts';

const stamp = iso => Date.parse(iso) / 1000;

test('intraday axis and crosshair show Pakistan exchange time regardless of browser timezone', () => {
  const originalZone = process.env.TZ;
  try {
    for (const zone of ['UTC', 'America/New_York', 'Asia/Karachi']) {
      process.env.TZ = zone;
      const labels = chartTimeFormatters(true);
      const time = stamp('2026-11-02T09:00:00Z');
      assert.equal(labels.tickMarkFormatter(time, TickMarkType.Time), '14:00');
      assert.equal(labels.timeFormatter(time), '02 Nov 2026 14:00 PKT');
      assert.equal(labels.tickMarkFormatter(stamp('2026-11-02T04:30:15Z'), TickMarkType.TimeWithSeconds), '09:30:15');
    }
  } finally {
    if (originalZone === undefined) delete process.env.TZ;
    else process.env.TZ = originalZone;
  }
});

test('Pakistan date rollover agrees on axis and crosshair', () => {
  const labels = chartTimeFormatters(true);
  const time = stamp('2026-12-31T20:00:00Z');
  assert.equal(labels.tickMarkFormatter(time, TickMarkType.Year), '2027');
  assert.equal(labels.tickMarkFormatter(time, TickMarkType.Month), 'Jan');
  assert.equal(labels.tickMarkFormatter(time, TickMarkType.DayOfMonth), '01');
  assert.equal(labels.timeFormatter(time), '01 Jan 2027 01:00 PKT');
});

test('daily bars and business days retain their session date when switching intervals', () => {
  const time = stamp('2026-11-02T00:00:00Z');
  assert.equal(chartTimeFormatters(false).timeFormatter(time), '02 Nov 2026');
  assert.equal(chartTimeFormatters(true).timeFormatter(time), '02 Nov 2026 05:00 PKT');
  for (const intraday of [false, true]) {
    const labels = chartTimeFormatters(intraday);
    assert.equal(labels.timeFormatter('2026-11-02'), '02 Nov 2026');
    assert.equal(labels.timeFormatter({ year: 2026, month: 11, day: 2 }), '02 Nov 2026');
    assert.equal(labels.tickMarkFormatter('2026-11-02', TickMarkType.DayOfMonth), null);
  }
});
