<script lang="ts">
  /**
   * Whether a triggered auto order goes out on its own or waits for the person, and — only where it
   * can change anything — the control that stops it asking for a while (the "approval window").
   *
   * The button used to show on every install and did nothing on most of them: the window is consulted
   * only under ApprovalRequired execution with Approval.Mode = Window, and Mode defaults to Always. The
   * server now says whether it applies (`windowApplies`), so the page never re-derives it from config.
   * An open window always shows its Close control, whatever else changed since it was opened.
   */
  import { createEventDispatcher } from 'svelte';
  import { ShieldAlert, ShieldCheck, Clock } from 'lucide-svelte';
  import { trading, type ArmedOrdersResponse } from './api';
  import HelpTip from './HelpTip.svelte';

  interface Props {
    approval: ArmedOrdersResponse['approval'];
  }

  let { approval }: Props = $props();
  const dispatch = createEventDispatcher<{ changed: void }>();
  let busy = $state(false);
  let message: string | null = $state(null);
  let failed = $state(false);

  // An older server sends neither field: keep the button rather than hide a control that may work.
  let windowApplies = $derived(approval.windowApplies ?? true);
  let open = $derived(!!approval.armedUntilUtc);
  let unattended = $derived(approval.willFireUnattended);
  let until = $derived(approval.armedUntilUtc ? new Date(approval.armedUntilUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '');

  async function openWindow() {
    if (busy) return;
    busy = true; message = null; failed = false;
    try {
      const result = await trading.approval.openWindow();
      // inForce, not "granted": a window opened while the market is shut does nothing.
      message = result.inForce ? null : result.note;
      dispatch('changed');
    } catch (e) {
      failed = true; message = e instanceof Error ? e.message : String(e);
    } finally { busy = false; }
  }

  async function closeWindow() {
    if (busy) return;
    busy = true; message = null; failed = false;
    try {
      await trading.approval.closeWindow();
      dispatch('changed');
    } catch (e) {
      failed = true; message = e instanceof Error ? e.message : String(e);
    } finally { busy = false; }
  }
</script>

<div class="approval" class:live={open} class:auto={!open && unattended === true}>
  {#if open || unattended !== true}<ShieldAlert size={13}/>{:else}<ShieldCheck size={13}/>{/if}
  <div class="copy">
    <b>
      {#if open}Sending without asking until {until}
      {:else if unattended === true}Triggered orders are sent automatically
      {:else if unattended === false}Triggered orders wait for your confirmation
      {:else}Confirmation: {approval.mode}{/if}
    </b>
    {#if !open && unattended === true && approval.mode === 'Auto'}
      <span>{approval.autoApprovedThisSession} of {approval.maxOrdersPerSession} sent today</span>
    {/if}
    {#if message}<span class:failed role="status">{message}</span>{/if}
  </div>
  <HelpTip label="Help about confirming auto orders" align="end">
    When a trigger is reached, the order is either sent straight away or held until you confirm it,
    depending on how automation is set up.
    {#if windowApplies}
      “Send without asking” stops it asking for a while (30 minutes unless set otherwise). It ends at
      that time, when you press “Ask me again”, or at the market close. Your limits and the kill switch
      still apply.
    {/if}
    {#if approval.explanation}<br/><br/>{approval.explanation}{/if}
  </HelpTip>
  {#if open}
    <button class="btn btn-danger" onclick={closeWindow} disabled={busy}>Ask me again</button>
  {:else if windowApplies}
    <button class="btn btn-ghost" onclick={openWindow} disabled={busy}><Clock size={13}/> Send without asking</button>
  {/if}
</div>

<style>
  .approval { display:flex; align-items:center; gap:.6rem; background:var(--surface-2);
    border-radius:var(--radius-sm); padding:.5rem .65rem; color:var(--text-3); }
  .approval.live { color:var(--warning); background:color-mix(in srgb, var(--warning) 10%, transparent); }
  .approval.auto { color:var(--success); }
  .copy { display:flex; flex-direction:column; gap:.15rem; flex:1; min-width:0; }
  b { color:var(--text); font-size:.78rem; font-weight:600; }
  span { font-size:.7rem; }
  span.failed { color:var(--danger); }
  .btn { white-space:nowrap; display:flex; align-items:center; gap:.3rem; }
</style>
