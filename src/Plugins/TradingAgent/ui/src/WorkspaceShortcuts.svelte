<script lang="ts">
  import { tick } from 'svelte';
  let dialog: HTMLDialogElement | undefined = $state();
  let back: HTMLButtonElement | undefined = $state();
  let previous: HTMLElement | null = null;
  export async function open() {
    previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialog!.showModal(); await tick(); back!.focus();
  }
  function close() { dialog!.close(); if (previous?.isConnected) previous.focus(); }
  const shortcuts = [
    ['Ctrl+Shift+K','Find and focus or open a panel by name or purpose (also from inputs)'],
    ['Ctrl+K','Search commands and open any panel (also from inputs)'],
    ['Ctrl+/','Open this shortcuts sheet'],
    ['F6 / Shift+F6','Next / previous panel group, including auto-hidden tools'],
    ['Ctrl+] / Ctrl+[','Next / previous tab in the focused group'],
    ['Ctrl+Shift+Space','Maximize / restore the active group'],
    ['Ctrl+Shift+F','Enter / exit page full screen; browser F11 remains browser-owned'],
    ['Ctrl+Shift+1','Open the Watchlist and focus stock search (also from inputs)'],
    ['Ctrl+Shift+2 / 3','Price chart / Trade plan'],
    ['Ctrl+Shift+4 / 5','Order ticket / Order logs'],
    ['Ctrl+Shift+6 / 7','Portfolio / Persistent orders'],
    ['Ctrl+Shift+0','Reset view (does not discard in-session drafts)'],
    ['Escape','Clear the watchlist search when focused there, close a dialog or bottom peek, or restore a maximized group.'],
    ['Tab / Shift+Tab','Move between controls; Enter / Space activates the focused control'],
    ['Watchlist: ↑ ↓ / Enter','Move from search into the matching rows or between rows. Enter opens the focused stock or the first search match.'],
    ['Watchlist: F2 / Shift+F10','Open actions for the focused symbol; / focuses search'],
    ['Order lists: ↑ ↓ / Home / End','Move between visible rows; Tab reaches filters and controls'],
    ['Persistent orders, repeat until filled: Shift+F10','Open safe row actions; selecting one opens its review or evidence form'],
    ['Order lists: Enter on symbol','Focus row controls, without activating them; Space selects a focused checkbox'],
    ['New Order: toolbar / Ctrl+K','Open the modal order composer; no order is submitted by opening it'],
    ['Order ticket: Ctrl+Enter','Validate and open review — never submit directly'],
    ['Order review: Tab → Confirm','Explicit confirmation; Escape returns without submitting']
  ];
</script>
<dialog bind:this={dialog} aria-label="Keyboard shortcuts" oncancel={event => {event.preventDefault();close();}}>
  <header><h2>Keyboard shortcuts</h2><button bind:this={back} onclick={close}>Close shortcuts</button></header>
  <p>Layout shortcuts do not place orders. Watchlist search, panel search, Commands and F6 work from inputs. On Mac, Cmd also works for watchlist search, panel search, Ctrl+K, number and maximize shortcuts.</p>
  <table><caption>Workstation keyboard reference</caption><tbody>{#each shortcuts as [keys,description]}<tr><th scope="row"><kbd>{keys}</kbd></th><td>{description}</td></tr>{/each}</tbody></table>
  <p>Use <b>Commands</b> for pin/unpin, resizing and moving panels. The workstation follows the AgentFox theme. Browser/OS shortcuts may take priority; every action also has an on-screen control. Native order controls retain explicit confirmation.</p>
</dialog>
<style>
  dialog { width:min(760px,calc(100vw - 2rem)); max-height:80dvh; padding:1rem; border:1px solid var(--border-md); border-radius:8px; background:var(--surface); color:var(--text); overflow:auto; }
  dialog::backdrop { background:#0008; } header { display:flex; align-items:center; justify-content:space-between; gap:1rem; } h2 { font-size:1rem; margin:0; }
  p,table { font-size:.75rem; line-height:1.5; } p { color:var(--text-2); } table { width:100%; border-collapse:collapse; text-align:left; }
  caption { text-align:left; padding:.5rem 0; font-weight:600; } th,td { padding:.45rem; border-bottom:1px solid var(--border); } th { width:40%; } kbd { font:inherit; font-weight:600; }
  button { padding:.4rem .6rem; cursor:pointer; background:var(--surface-2); border:1px solid var(--border-md); border-radius:4px; color:var(--text); } button:focus-visible { outline:2px solid var(--primary); }
</style>
