<script lang="ts">
  import { tick } from 'svelte';
  let dialog: HTMLDialogElement;
  let back: HTMLButtonElement;
  let previous: HTMLElement | null = null;
  export async function open() {
    previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialog.showModal(); await tick(); back.focus();
  }
  function close() { dialog.close(); if (previous?.isConnected) previous.focus(); }
  const shortcuts = [
    ['Ctrl+K','Search commands and open any panel (also from inputs)'],
    ['Ctrl+/','Open this shortcuts sheet'],
    ['F6 / Shift+F6','Next / previous panel group, including auto-hidden tools'],
    ['Ctrl+] / Ctrl+[','Next / previous tab in the focused group'],
    ['Ctrl+Shift+Space','Maximize / restore the active group'],
    ['Ctrl+Shift+F','Enter / exit page full screen; browser F11 remains browser-owned'],
    ['Ctrl+Shift+1 / 2 / 3','Watchlist / Price chart / Trade plan'],
    ['Ctrl+Shift+4 / 5','Order ticket / Order logs'],
    ['Ctrl+Shift+6 / 7 / 8','Portfolio / Persistent orders / Armed orders'],
    ['Ctrl+Shift+0','Reset view (does not discard in-session drafts)'],
    ['Escape','Close a dialog or bottom peek; otherwise restore a maximized group'],
    ['Tab / Shift+Tab','Move between controls; Enter / Space activates the focused control'],
    ['Watchlist: ↑ ↓ / Enter','Move between symbols / select the focused symbol'],
    ['Watchlist: F2 / Shift+F10','Open actions for the focused symbol; / focuses search'],
    ['Order lists: ↑ ↓ / Home / End','Move between visible rows; Tab reaches filters and controls'],
    ['Persistent orders: Shift+F10','Open safe row actions; selecting one opens its review or evidence form'],
    ['Order lists: Enter on symbol','Focus row controls, without activating them; Space selects a focused checkbox'],
    ['Order ticket: Ctrl+Enter','Validate and open review — never submit directly'],
    ['Order review: Tab → Confirm','Explicit confirmation; Escape returns without submitting']
  ];
</script>
<dialog bind:this={dialog} aria-label="Keyboard shortcuts" on:cancel={event => {event.preventDefault();close();}}>
  <header><h2>Keyboard shortcuts</h2><button bind:this={back} on:click={close}>Close shortcuts</button></header>
  <p>Layout shortcuts do not place orders. Number, tab-cycle and maximize shortcuts are ignored while typing. Commands and F6 remain available from inputs. On Mac, Cmd also works for the Ctrl+K, number and maximize shortcuts.</p>
  <table><caption>Workstation keyboard reference</caption><tbody>{#each shortcuts as [keys,description]}<tr><th scope="row"><kbd>{keys}</kbd></th><td>{description}</td></tr>{/each}</tbody></table>
  <p>Use <b>Commands</b> for pin/unpin, theme, resizing and moving panels. Browser/OS shortcuts may take priority; every action also has an on-screen control. Native order controls retain explicit confirmation.</p>
</dialog>
<style>
  dialog { width:min(760px,calc(100vw - 2rem)); max-height:80dvh; padding:1rem; border:1px solid var(--border-md); border-radius:8px; background:var(--surface); color:var(--text); overflow:auto; }
  dialog::backdrop { background:#0008; } header { display:flex; align-items:center; justify-content:space-between; gap:1rem; } h2 { font-size:1rem; margin:0; }
  p,table { font-size:.75rem; line-height:1.5; } p { color:var(--text-2); } table { width:100%; border-collapse:collapse; text-align:left; }
  caption { text-align:left; padding:.5rem 0; font-weight:600; } th,td { padding:.45rem; border-bottom:1px solid var(--border); } th { width:40%; } kbd { font:inherit; font-weight:600; }
  button { padding:.4rem .6rem; cursor:pointer; background:var(--surface-2); border:1px solid var(--border-md); border-radius:4px; color:var(--text); } button:focus-visible { outline:2px solid var(--primary); }
</style>
