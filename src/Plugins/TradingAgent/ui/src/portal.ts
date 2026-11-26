/**
 * Moves an element to the end of `document.body` while it is mounted, so a full-screen overlay opened from
 * inside a docked panel covers the page instead of the panel.
 *
 * Why it is needed: dockview draws every panel in a `.dv-render-overlay`, which sets `contain: layout paint`
 * and a `transform`. Either one makes that box the containing block for `position: fixed` descendants and
 * clips them to it, so `inset: 0` means "the panel", not "the screen". A dialog taller than the panel then
 * loses its header and footer — the Edit window on an armed rule order lost its Close, Cancel and Save
 * buttons that way (2026-11-26).
 *
 * Apply it to an element that is NOT a component's root node: Svelte 5 removes a block's DOM by walking
 * siblings from its first node, so a moved root node would take the siblings it now has in `<body>` with it.
 * Wrap the overlay in a `display: contents` host and move the overlay itself.
 */
export function portal(node: Element, enabled: boolean = true) {
  let placeholder: Comment | null = null;

  function attach() {
    if (placeholder || typeof document === 'undefined' || !node.parentNode) return;
    placeholder = document.createComment('portal');
    node.parentNode.insertBefore(placeholder, node);
    document.body.appendChild(node);
  }

  function detach() {
    if (!placeholder) return;
    if (placeholder.parentNode) placeholder.parentNode.insertBefore(node, placeholder);
    else node.remove();
    placeholder.remove();
    placeholder = null;
  }

  if (enabled) attach();

  return {
    update(next: boolean = true) { if (next) attach(); else detach(); },
    // The host was removed in place; the moved node is ours to remove, or it would stay on the page.
    destroy() { if (placeholder) { node.remove(); placeholder.remove(); placeholder = null; } }
  };
}
