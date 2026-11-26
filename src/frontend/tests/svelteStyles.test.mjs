import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// svelte/compiler's migrate() (what `sv migrate svelte-5` runs) rewrote styles it should have left alone
// (2026-11-26): `var(--warning)` became `var(:global(--warning))`, which the production CSS minifier
// refuses (CI's build failed on it), and `:is(input, button)` became `:is(:global(input, button))`,
// which silently restyles every matching element on the page instead of the component's own.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src');
const files = [];
(function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full);
    else if (entry.name.endsWith('.svelte')) files.push(full);
  }
})(root);

test('no component style wraps a custom property or an :is() list in :global()', () => {
  const found = [];
  for (const file of files) {
    const style = fs.readFileSync(file, 'utf8').match(/<style[^>]*>([\s\S]*?)<\/style>/)?.[1] ?? '';
    style.split('\n').forEach((line, i) => {
      if (/var\(\s*:global\(|:is\(\s*:global\(/.test(line))
        found.push(`${path.relative(root, file).replaceAll('\\', '/')}: ${line.trim()}`);
    });
  }
  assert.deepEqual(found, []);
});
