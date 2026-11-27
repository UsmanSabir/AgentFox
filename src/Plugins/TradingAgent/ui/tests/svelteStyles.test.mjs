import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// svelte/compiler's migrate() (what `sv migrate svelte-5` runs) rewrote styles it should have left alone
// (2026-11-26): `var(--warning)` became `var(:global(--warning))`, which the production CSS minifier
// refuses (CI's build failed on it); `:is(input, button)` became `:is(:global(input, button))`, which
// silently restyles every matching element on the page instead of the component's own; and
// `min(210px,100%)` became `min(:global(210px,100%))`. Any `:global(` directly inside a parenthesis is
// flagged, apart from the selector forms Svelte supports there (`:not(`, `:has(`, `:where(`).
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src');
const files = [];
(function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full);
    else if (entry.name.endsWith('.svelte')) files.push(full);
  }
})(root);

test('no component style wraps a CSS function argument or an :is() list in :global()', () => {
  const found = [];
  for (const file of files) {
    const style = fs.readFileSync(file, 'utf8').match(/<style[^>]*>([\s\S]*?)<\/style>/)?.[1] ?? '';
    style.split('\n').forEach((line, i) => {
      if (/(?<!:(?:not|has|where))\(\s*:global\(/.test(line))
        found.push(`${path.relative(root, file).replaceAll('\\', '/')}: ${line.trim()}`);
    });
  }
  assert.deepEqual(found, []);
});
