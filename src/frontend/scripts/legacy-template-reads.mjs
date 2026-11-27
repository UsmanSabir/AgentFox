// Finds the one way a legacy-mode (non-runes) Svelte 5 component shows stale text without any error.
//
// In legacy mode a template call such as `{nowText(condition)}` compiles to `untrack(() => nowText(condition))`, with
// only the variables written in the template as its dependencies. If `nowText` reads other component state, a change
// to that state re-renders nothing. The Rules editor's per-condition answers stayed blank for exactly this reason
// (2026-11-26). Runes-mode components track reads at run time and are skipped.
//
// Usage: node scripts/legacy-template-reads.mjs [folder]   (default: this UI's src; exits 1 and lists every finding)
// The UI test suite runs the same scan (tests/legacyTemplateReads.test.mjs). Copied from the premium edition's UI,
// where it found the first two cases (2026-11-26).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compile } from 'svelte/compiler';

/** True for a component written in runes mode, where none of this applies. */
export const isRunes = source => /\$state\(|\$props\(|\$derived|\$effect\(/.test(source);

const balanced = (code, start, open, close) => {
  let i = start, depth = 1;
  while (depth && i < code.length) { if (code[i] === open) depth++; else if (code[i] === close) depth--; i++; }
  return i;
};

/** Removes the bodies of `=> { ... }` handlers: a call inside one runs on an event, not while rendering. */
function withoutHandlerBodies(expr) {
  let out = '', i = 0;
  for (;;) {
    const at = expr.indexOf('=> {', i);
    if (at < 0) return out + expr.slice(i);
    out += expr.slice(i, at + 2);
    i = balanced(expr, at + 4, '{', '}');
  }
}

/**
 * Every template call in one component that reads state it is not given, as
 * `{ fn, reads: string[], expression }`. Empty for a runes-mode component.
 */
export function scanSource(source, filename = 'Component.svelte') {
  if (isRunes(source)) return [];
  const code = compile(source, { generate: 'client', filename }).js.code;
  const cells = new Set([...code.matchAll(/(?:const|let) (\w+) = \$\.(?:mutable_source|mutable_state|source|state)\(/g)].map(m => m[1]));
  const fns = Object.create(null);
  for (const m of code.matchAll(/^\t(?:async )?function (\w+)\([^)]*\)\s*\{/gm)) {
    const start = m.index + m[0].length;
    fns[m[1]] = code.slice(start, balanced(code, start, '{', '}'));
  }
  for (const m of code.matchAll(/^\t(?:const|let) (\w+) = (?:async )?(?:\([^)]*\)|\w+) =>/gm)) {
    if (fns[m[1]]) continue;
    const rest = code.slice(m.index + m[0].length);
    const stop = rest.search(/\n\t\S/);
    fns[m[1]] = stop < 0 ? rest : rest.slice(0, stop);
  }
  // A component `let` the template never mentions is not reactive at all in legacy mode: it compiles to a plain
  // variable. One that is reassigned later and read by a template call is the same stale text, without even a signal.
  const plain = new Set([...code.matchAll(/^\tlet (\w+)(?: = (?!\$\.)[^;]*)?;/gm)].map(m => m[1])
    .filter(v => new RegExp(`(?<![.\\w])${v} (?:[-+*/]?=)(?!=)`).test(code.replace(new RegExp(`^\\tlet ${v}\\b`, 'm'), ''))));
  const reads = name => {
    const body = fns[name] ?? '';
    const found = new Set([...body.matchAll(/\$\.get\((\w+)\)/g)].map(g => g[1]).filter(v => cells.has(v)));
    for (const v of plain) if (new RegExp(`(?<![.\\w$])${v}\\b`).test(body)) found.add(v);
    return found;
  };
  const closure = (name, seen = new Set()) => {
    if (seen.has(name)) return new Set();
    seen.add(name);
    const all = reads(name);
    for (const c of (fns[name] ?? '').matchAll(/\b(\w+)\(/g))
      if (fns[c[1]] && c[1] !== name) for (const v of closure(c[1], seen)) all.add(v);
    return all;
  };

  const findings = [], seen = new Set();
  for (let k = code.indexOf('$.untrack(() =>'); k >= 0; k = code.indexOf('$.untrack(() =>', k)) {
    const start = k + '$.untrack('.length, end = balanced(code, start, '(', ')');
    const expr = withoutHandlerBodies(code.slice(start + 5, end - 1));
    k = end;
    for (const c of expr.matchAll(/\b(\w+)\(/g)) {
      const fn = c[1];
      if (!fns[fn]) continue;
      const missing = [...closure(fn)].filter(v => !new RegExp(`\\b${v}\\b`).test(expr));
      const key = `${fn}:${missing.join(',')}`;
      if (missing.length && !seen.has(key)) {
        seen.add(key);
        findings.push({ fn, reads: missing, expression: expr.replace(/\s+/g, ' ').trim().slice(0, 120) });
      }
    }
  }
  // `$: x = f(a)` compiles to `legacy_pre_effect(() => (a), () => { x = f(a) })`: re-run only when `a` changes. A
  // function it calls that reads other state goes stale the same way (the Market watch table did not re-sort on a
  // header click, 2026-11-26). Callbacks inside the body run with it, so nothing is stripped here.
  //
  // Only a statement that DERIVES a value (`$: x = ...`) is checked. One that triggers an action (`$: load(a)`,
  // `$: if (a) refresh()`) is meant to ignore its own bookkeeping — timers, request tokens, "already loaded" marks —
  // and reacting to those would loop. A variable that is only ever called (a stored unsubscribe) is not data.
  const calledOnly = (v, body) => !new RegExp(`(?<![.\\w$])${v}\\b(?!\\s*\\()(?!\\s*=[^=])`).test(body);
  for (let k = code.indexOf('$.legacy_pre_effect('); k >= 0; k = code.indexOf('$.legacy_pre_effect(', k)) {
    const start = k + '$.legacy_pre_effect('.length, end = balanced(code, start, '(', ')');
    const whole = code.slice(start, end - 1);
    k = end;
    const split = whole.indexOf('() => {', whole.indexOf('=>') + 2);
    if (split < 0) continue;
    const deps = whole.slice(0, split), body = whole.slice(split);
    if (!/^\(\) => \{\s*\$\.set\(/.test(body)) continue;
    for (const c of body.matchAll(/\b(\w+)\(/g)) {
      const fn = c[1];
      if (!fns[fn]) continue;
      const missing = [...closure(fn)].filter(v => !new RegExp(`\\b${v}\\b`).test(deps))
        .filter(v => cells.has(v) || !calledOnly(v, Object.values(fns).join('\n')));
      const key = `$:${fn}:${missing.join(',')}`;
      if (missing.length && !seen.has(key)) {
        seen.add(key);
        findings.push({ fn, reads: missing, expression: `$: ${body.replace(/\s+/g, ' ').trim().slice(8, 120)}` });
      }
    }
  }
  return findings;
}

/** Every `.svelte` file under `root`, with its findings, for the files that have any. */
export function scanTree(root) {
  const files = [];
  (function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith('.svelte')) files.push(full);
    }
  })(root);
  return files
    .map(file => ({ file: path.relative(root, file).replaceAll('\\', '/'), findings: scanSource(fs.readFileSync(file, 'utf8'), file) }))
    .filter(r => r.findings.length);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src');
  const results = scanTree(root);
  for (const { file, findings } of results)
    for (const f of findings) console.log(`${file}: ${f.fn}(...) reads ${f.reads.join(', ')} but the template gives it only: ${f.expression}`);
  if (results.length) {
    console.log('\nPass the values in as arguments, or convert the component to runes mode.');
    process.exit(1);
  }
  console.log('No legacy template call reads state it is not given.');
}
