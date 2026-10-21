import { test } from 'node:test';
import assert from 'node:assert/strict';
import { detailCardPosition, markerDetailsByTime, markerPlacement, markerShape } from '../src/chartOverlayMarkers.ts';

test('a bottom marker goes in the event strip, and an unknown position above its bar', () => {
  assert.equal(markerPlacement('bottom'), 'strip');
  assert.equal(markerPlacement('belowBar'), 'belowBar');
  assert.equal(markerPlacement('aboveBar'), 'aboveBar');
  assert.equal(markerPlacement(undefined), 'aboveBar');
  assert.equal(markerPlacement('sideways'), 'aboveBar');
});

test('a marker draws the shape it asks for, and a circle when it asks for none or an unknown one', () => {
  assert.equal(markerShape('square'), 'square');
  assert.equal(markerShape('arrowUp'), 'arrowUp');
  assert.equal(markerShape(null), 'circle');
  assert.equal(markerShape(undefined), 'circle');
  assert.equal(markerShape('pentagon'), 'circle');
});

test('every marker on a bar contributes its detail, and markers without detail show no card', () => {
  const byTime = markerDetailsByTime([
    { time: 200, kind: 'earnings', detail: 'Results announced 4 Sep 2026: Year ended June 30, 2026' },
    { time: 100, kind: 'target', detail: null },
    { time: 200, kind: 'dividend', detail: 'Ex-dividend 8 Oct 2026: Rs 6.00 a share' },
    { time: 300, kind: 'dividend', detail: '   ' }
  ]);

  assert.deepEqual([...byTime.keys()], [200]);
  assert.deepEqual(byTime.get(200).map(l => l.kind), ['earnings', 'dividend']);
});

test('the same detail twice on one bar is shown once', () => {
  const byTime = markerDetailsByTime([
    { time: 1, kind: 'dividend', detail: 'Ex-dividend' },
    { time: 1, kind: 'dividend', detail: 'Ex-dividend' }
  ]);
  assert.equal(byTime.get(1).length, 1);
});

test('the card sits right of the pointer and flips left near the edge, staying inside the plot', () => {
  const plot = { width: 600, height: 400 };
  assert.deepEqual(detailCardPosition({ x: 100, y: 50 }, plot), { left: 114, top: 64 });
  assert.deepEqual(detailCardPosition({ x: 500, y: 50 }, plot), { left: 246, top: 64 });
  assert.deepEqual(detailCardPosition({ x: 10, y: 390 }, plot), { left: 24, top: 336 });
  assert.deepEqual(detailCardPosition({ x: 100, y: 50 }, { width: 200, height: 40 }), { left: 0, top: 0 });
});
