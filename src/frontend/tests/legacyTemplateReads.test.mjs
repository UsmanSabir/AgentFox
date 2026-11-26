import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { scanSource, scanTree } from '../scripts/legacy-template-reads.mjs';

// The Rules editor's defect in miniature: `{label(item)}` re-runs only when `item` changes, so `suffix` is never seen.
const stale = `<script>
  export let item = 'a';
  let suffix = '';
  function label(x) { return x + suffix; }
  function later() { suffix = '!'; }
</script>
<p>{label(item)}</p><button on:click={later}>go</button>`;

test('finds a template call that reads state it is not given', () => {
  const findings = scanSource(stale);
  assert.equal(findings.length, 1);
  assert.equal(findings[0].fn, 'label');
  assert.deepEqual(findings[0].reads, ['suffix']);
});

test('passing the state in as an argument clears it', () => {
  const fixed = stale.replace('function label(x)', 'function label(x, suffix)').replace('{label(item)}', '{label(item, suffix)}');
  assert.deepEqual(scanSource(fixed), []);
});

test('a call inside an event handler is not a render-time read', () => {
  const handler = stale.replace('<p>{label(item)}</p>', '<Child onpick={item ? () => { label(item); } : null} />');
  assert.deepEqual(scanSource(`${handler}`.replace('<script>', '<script>\n  import Child from "./Child.svelte";'), 'Handler.svelte'), []);
});

// The Market watch table's defect: `$: rows = sorted(items)` re-runs only for `items`, so a new sort order waits.
const derived = `<script>
  export let items = [];
  let order = 'asc';
  function sorted(list) { return order === 'asc' ? list : [...list].reverse(); }
  $: rows = sorted(items);
</script>
{#each rows as r}<p>{r}</p>{/each}<button on:click={() => order = 'desc'}>flip</button>`;

test('finds a $: derived value whose function reads state the statement does not name', () => {
  const findings = scanSource(derived);
  assert.equal(findings.length, 1);
  assert.equal(findings[0].fn, 'sorted');
  assert.deepEqual(findings[0].reads, ['order']);
});

test('a $: statement that triggers an action may ignore its own bookkeeping', () => {
  const trigger = `<script>
  export let symbol = '';
  let token = 0;
  let data = null;
  async function load(s) { const mine = ++token; const r = await fetch(s); if (mine === token) data = r; }
  $: load(symbol);
</script>
<p>{data}</p>`;
  assert.deepEqual(scanSource(trigger), []);
});

test('a runes-mode component is skipped: it tracks reads at run time', () => {
  const runes = `<script>
  let { item } = $props();
  let suffix = $state('');
  function label(x) { return x + suffix; }
</script>
<p>{label(item)}</p>`;
  assert.deepEqual(scanSource(runes), []);
});

test('no host app component has a stale template call', () => {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src');
  const results = scanTree(root);
  assert.deepEqual(results.map(r => `${r.file}: ${r.findings.map(f => `${f.fn} reads ${f.reads.join(', ')}`).join('; ')}`), []);
});
