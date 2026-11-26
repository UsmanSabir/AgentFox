import test from 'node:test';
import assert from 'node:assert/strict';
import { portal } from '../src/portal.ts';

// The smallest DOM the action touches: parents, ordered children, insertBefore, appendChild, remove.
function node(name) {
  const n = {
    name, parentNode: null, children: [],
    appendChild(child) { child.remove(); child.parentNode = n; n.children.push(child); return child; },
    insertBefore(child, ref) {
      child.remove(); child.parentNode = n;
      const at = n.children.indexOf(ref);
      n.children.splice(at < 0 ? n.children.length : at, 0, child);
      return child;
    },
    remove() {
      if (!n.parentNode) return;
      const siblings = n.parentNode.children;
      siblings.splice(siblings.indexOf(n), 1);
      n.parentNode = null;
    }
  };
  return n;
}

function page() {
  const body = node('body');
  globalThis.document = { body, createComment: text => node(`#${text}`) };
  const panel = node('panel');       // a dockview panel: clips anything fixed inside it
  const host = node('host');         // the composer's display:contents root
  const backdrop = node('backdrop');
  body.appendChild(panel); panel.appendChild(host); host.appendChild(backdrop);
  const later = node('later');       // something else the page appends to <body> afterwards
  return { body, panel, host, backdrop, later };
}

test('a pop-up leaves the panel that would clip it and covers the page', () => {
  const { body, host, backdrop } = page();
  portal(backdrop, true);
  assert.equal(backdrop.parentNode, body);
  assert.deepEqual(host.children.map(c => c.name), ['#portal'], 'a placeholder keeps its place in the host');
});

test('a docked composer stays where it was put', () => {
  const { host, backdrop } = page();
  portal(backdrop, false);
  assert.equal(backdrop.parentNode, host);
  assert.equal(host.children.length, 1);
});

test('closing the pop-up removes only the pop-up, never what follows it in <body>', () => {
  const { body, panel, host, backdrop, later } = page();
  const action = portal(backdrop, true);
  body.appendChild(later);
  panel.children.splice(panel.children.indexOf(host), 1); host.parentNode = null; // Svelte removes the host in place
  action.destroy();
  assert.equal(backdrop.parentNode, null);
  assert.deepEqual(body.children.map(c => c.name), ['panel', 'later']);
});

test('switching it off puts it back exactly where it was', () => {
  const { host, backdrop } = page();
  const action = portal(backdrop, true);
  action.update(false);
  assert.deepEqual(host.children.map(c => c.name), ['backdrop']);
  action.update(true);
  action.update(true);
  assert.deepEqual(host.children.map(c => c.name), ['#portal'], 'enabling twice does not leave two placeholders');
});
