import test from 'node:test';
import assert from 'node:assert/strict';
import { nextTheme, resolveInitialTheme } from '../src/lib/theme.js';

test('dark is the default without a saved choice', () => {
  assert.equal(resolveInitialTheme(null), 'dark');
  assert.equal(resolveInitialTheme('system'), 'dark');
});

test('an explicit light preference is respected', () => {
  assert.equal(resolveInitialTheme('light'), 'light');
});

test('theme toggle switches between the two supported themes', () => {
  assert.equal(nextTheme('dark'), 'light');
  assert.equal(nextTheme('light'), 'dark');
});
