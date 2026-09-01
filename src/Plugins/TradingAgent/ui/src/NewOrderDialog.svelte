<script lang="ts">
  import { createEventDispatcher, onMount } from 'svelte';
  import {
    trading, percentTriggerLevel,
    type OrderIntentDefinition, type OrderIntentRegistryResponse, type WatchlistEntry,
    type TriggerKind, type ArmOrderRequest, type BrokerAccountSnapshot
  } from './api';
  import {
    ShoppingCart, X, AlertTriangle, RefreshCw, CheckCircle2, Clock3, BriefcaseBusiness, Wallet
  } from 'lucide-svelte';

  export let selectedSymbol: string | null = null;

  const dispatch = createEventDispatcher<{ close: void; changed: void }>();
  let dialogElement: HTMLDivElement;
  let registry: OrderIntentRegistryResponse | null = null;
  let symbols: WatchlistEntry[] = [];
  let choice: OrderIntentDefinition | null = null;
  let symbol = selectedSymbol ?? '';
  let sizeMode: 'shares' | 'value' = 'shares';
  let quantity: number | null = null;
  let orderValue: number | null = null;
  let currentPrice: number | null = null;
  /**
   * The symbol the price fields were last seeded from.
   *
   * `applySuggestedPrices` deliberately never chases the market once a person has typed a number, and
   * that is right for a re-quote of the SAME instrument. It was wrong across a symbol CHANGE: the
   * previous stock's price stayed in the limit field while the "Latest price" readout beside it updated,
   * so the two disagreed and the stale one was the one being submitted. Reported live 2026-09-01 —
   * a SELL limit of 104 left over from another symbol, against a SYS market around 124.
   */
  let pricedSymbol: string | null = null;
  let price: number | null = null;
  let triggerPrice: number | null = null;
  let limitPrice: number | null = null;
  let triggerPercent: number | null = null;
  let expiresInDays = 30;
  let persistentUntilFilled = false;
  let loading = true;
  let quoteBusy = false;
  let holdingBusy = false;
  let holdingLoaded = false;
  let account: BrokerAccountSnapshot | null = null;
  let holdingError: string | null = null;
  let busy = false;
  let error: string | null = null;
  let result: { ok: boolean; title: string; detail: string; executionId?: string } | null = null;
  let clientRequestId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;

  const money = (value: number) => value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  const when = (value: string) => new Date(value).toLocaleString();
  $: categories = [...new Set((registry?.intents ?? []).map(item => item.category))];

  onMount(async () => {
    dialogElement.focus();
    try {
      // Independent failure domains: the watchlist is a symbol-picker convenience, while the
      // order-intent registry is what actually renders the "choose what you want to happen" grid.
      // A slow/failed watchlist (e.g. a cold dashboard load contending for the same DB and an
      // uncached upstream quote fetch) must not block order placement entirely — only degrade the
      // symbol datalist to free-text entry.
      const [intentResult, watchlistResult] = await Promise.allSettled([
        trading.orderIntents(), trading.watchlist.list()
      ]);
      if (intentResult.status === 'rejected') throw intentResult.reason;
      registry = intentResult.value;
      if (watchlistResult.status === 'fulfilled') {
        symbols = watchlistResult.value.entries.filter(entry => entry.tradable);
        if (!symbol) symbol = symbols[0]?.symbol ?? '';
      } else {
        error = `Watchlist unavailable (symbol picker has no suggestions): ${
          watchlistResult.reason instanceof Error ? watchlistResult.reason.message : String(watchlistResult.reason)
        }`;
      }
      if (symbol) await refreshQuote();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  });

  async function refreshQuote() {
    if (!symbol.trim() || quoteBusy) return;
    quoteBusy = true;
    error = null;
    try {
      const chart = await trading.candles(symbol.trim().toUpperCase(), '1D');
      symbol = chart.symbol;
      currentPrice = chart.snapshot.close;
      // A different instrument makes every price field stale, whoever typed it: a number that was
      // deliberate for one stock is arbitrary for another, and on a SELL an arbitrarily low limit sells
      // below market. Re-seeding is the safe direction. The manual refresh button lands here too, but
      // with the same symbol, so a typed price is still left alone.
      const changed = pricedSymbol !== null && pricedSymbol !== chart.symbol.trim().toUpperCase();
      pricedSymbol = chart.symbol.trim().toUpperCase();
      applySuggestedPrices(changed);
    } catch (e) {
      currentPrice = null;
      error = `Latest price unavailable: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      quoteBusy = false;
    }
  }

  function choose(intent: OrderIntentDefinition) {
    choice = intent;
    price = null;
    triggerPrice = null;
    limitPrice = null;
    triggerPercent = intent.defaultPercent ?? null;
    result = null;
    error = null;
    clientRequestId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    applySuggestedPrices(true);
  }

  /**
   * Starting values only. Once a person edits a number this function never chases the market — EXCEPT
   * when `force` is set, which means the numbers on screen belong to a different order intent or a
   * different symbol and are therefore not that person's answer to the question now being asked.
   */
  function applySuggestedPrices(force: boolean) {
    if (!choice || !currentPrice || currentPrice <= 0) return;
    if (choice.priceField === 'limit' && (force || price == null)) price = currentPrice;
    if (choice.priceField === 'target' && (force || price == null))
      price = Number((currentPrice * 1.05).toFixed(2));
    if (choice.priceField === 'stop' && (force || triggerPrice == null)) {
      triggerPrice = Number((currentPrice * (choice.action === 'SELL' ? .98 : 1.02)).toFixed(2));
      limitPrice = Number((triggerPrice * (choice.action === 'SELL' ? .99 : 1.01)).toFixed(2));
    }
  }

  const sameSymbol = (candidate: string | null | undefined, ticker: string) =>
    candidate?.trim().toUpperCase() === ticker;

  async function loadHolding() {
    if (holdingBusy) return;
    holdingBusy = true;
    holdingError = null;
    try {
      account = await trading.account();
      holdingLoaded = true;
    } catch (e) {
      holdingError = e instanceof Error ? e.message : String(e);
    } finally {
      holdingBusy = false;
    }
  }

  function sellFraction(fraction: number) {
    if (availableSellQuantity == null || availableSellQuantity <= 0) return;
    const shares = fraction >= 1
      ? availableSellQuantity
      : Math.floor(availableSellQuantity * fraction);
    if (shares <= 0) return;
    sizeMode = 'shares';
    quantity = shares;
  }

  function buyFraction(fraction: number) {
    if (buyingPower == null || buyingPower <= 0) return;
    const value = Math.floor(buyingPower * fraction);
    if (value <= 0) return;
    // Value mode rather than shares: buying power is money, and converting it to a share count here
    // would have to guess a price the order may not use.
    sizeMode = 'value';
    orderValue = value;
  }

  $: conditionalLevel = choice?.submission === 'conditional' && choice.triggerKind
    ? percentTriggerLevel(choice.triggerKind, currentPrice, triggerPercent)
    : null;
  $: marketDisabled = choice?.orderType === 'MARKET'
    && registry != null && !registry.capabilities.marketOrdersEnabled;
  $: persistable = choice != null && choice.orderType !== 'MARKET';
  $: if (!persistable) persistentUntilFilled = false;
  $: selectedTicker = symbol.trim().toUpperCase();
  $: matchingHoldings = account?.holdings.filter(
    holding => sameSymbol(holding.symbol ?? holding.instrumentId, selectedTicker)) ?? [];
  $: matchingSellOrders = account?.orders.filter(order =>
    sameSymbol(order.symbol ?? order.instrumentId, selectedTicker)
    && (order.side?.toUpperCase() === 'SELL' || order.side?.toUpperCase() === 'SEL')) ?? [];
  $: heldQuantity = holdingLoaded && account?.holdingsAvailable
    && matchingHoldings.every(holding => holding.quantity != null)
      ? Math.floor(matchingHoldings.reduce((sum, holding) => sum + Math.max(0, holding.quantity ?? 0), 0))
      : null;
  $: committedSellQuantity = holdingLoaded && account?.ordersAvailable
    && matchingSellOrders.every(order => order.remainingQuantity != null)
      ? Math.floor(matchingSellOrders.reduce(
          (sum, order) => sum + Math.max(0, order.remainingQuantity ?? 0), 0))
      : null;
  $: availableSellQuantity = heldQuantity != null && committedSellQuantity != null
    ? Math.max(0, heldQuantity - committedSellQuantity)
    : null;
  // The BUY-side counterpart of availableSellQuantity. `available_cash` is the broker contract's
  // stable key for "what this account may commit right now" (see IBrokerAccountReader) — deliberately
  // not a cash balance, which can be a very different number when unsettled sale proceeds are spendable
  // or resting orders have already committed part of it.
  $: buyingPowerBalance = holdingLoaded && account?.balancesAvailable
    ? account.balances.find(entry => entry.key === 'available_cash') ?? null
    : null;
  $: buyingPower = buyingPowerBalance?.value ?? null;
  // Informational, never a block: the broker and the risk engine decide, and this snapshot can be
  // seconds stale. Telling the operator early is worth much more than refusing here would be.
  $: buyExceedsBuyingPower = choice?.action === 'BUY' && buyingPower != null
    && estimatedValue != null && estimatedValue > buyingPower;
  $: estimatedPrice = choice?.orderType === 'MARKET' ? currentPrice
    : choice?.orderType === 'STOPLOSS' ? triggerPrice
    : choice?.submission === 'conditional' ? conditionalLevel : price;
  $: availableSellValue = availableSellQuantity != null && estimatedPrice && estimatedPrice > 0
    ? availableSellQuantity * estimatedPrice
    : null;
  $: valueSizedQuantity = sizeMode === 'value' && orderValue && orderValue > 0 && estimatedPrice && estimatedPrice > 0
    ? Math.round(orderValue / estimatedPrice)
    : null;
  $: effectiveQuantity = sizeMode === 'value' ? valueSizedQuantity : quantity;
  $: estimatedValue = effectiveQuantity && estimatedPrice ? effectiveQuantity * estimatedPrice : null;
  $: sellExceedsAvailable = choice?.action === 'SELL'
    && effectiveQuantity != null && availableSellQuantity != null
    && effectiveQuantity > availableSellQuantity;
  $: sizingDifference = sizeMode === 'value' && orderValue && estimatedValue != null
    ? estimatedValue - orderValue
    : null;

  $: summary = (() => {
    if (!choice || !symbol || !effectiveQuantity) return null;
    const side = choice.action === 'BUY' ? 'Buy' : 'Sell';
    if (choice.submission === 'conditional' && conditionalLevel != null) {
      const move = choice.triggerKind === 'PercentDrop' ? 'falls' : 'rises';
      const trail = choice.trailing ? ' from the highest price seen after arming' : '';
      return `${side} ${effectiveQuantity} ${symbol} if it ${move} ${triggerPercent}%${trail} `
        + `(currently ${money(conditionalLevel)}).`;
    }
    if (choice.orderType === 'MARKET')
      return `${side} ${effectiveQuantity} ${symbol} now at the best available price.`;
    if (choice.orderType === 'STOPLOSS')
      return `${side} ${effectiveQuantity} ${symbol} when it reaches ${triggerPrice ?? '—'}; `
        + `once triggered, accept no worse than ${limitPrice ?? '—'}.`;
    return `${side} ${effectiveQuantity} ${symbol} at ${price ?? '—'} or better.`;
  })();

  async function submit() {
    if (!choice || busy) return;
    error = null;
    if (!symbol.trim()) { error = 'Choose a symbol.'; return; }
    if (sizeMode === 'value') {
      if (!orderValue || orderValue <= 0) { error = 'Enter a positive order value.'; return; }
      if (!estimatedPrice || estimatedPrice <= 0) {
        error = 'A current or order price is required to convert PKR value into shares.'; return;
      }
      if (!valueSizedQuantity || valueSizedQuantity <= 0) {
        error = `${money(orderValue)} PKR is too small for one share at ${money(estimatedPrice)} PKR.`; return;
      }
    } else if (!quantity || quantity <= 0 || !Number.isInteger(quantity)) {
      error = 'Enter a positive whole-share quantity.'; return;
    }
    const submittedQuantity = effectiveQuantity;
    if (!submittedQuantity || submittedQuantity <= 0) { error = 'Enter a valid order size.'; return; }
    if (marketDisabled) { error = 'Market orders are disabled in broker settings.'; return; }
    if (choice.submission === 'conditional') {
      if (!choice.triggerKind || !triggerPercent || triggerPercent <= 0 || triggerPercent > 50) {
        error = 'Enter a trigger move between 0 and 50%.'; return;
      }
      if (!currentPrice || !conditionalLevel) {
        error = 'A current price is required to arm a percentage trigger.'; return;
      }
    } else if (choice.orderType === 'STOPLOSS') {
      if (!triggerPrice || triggerPrice <= 0 || !limitPrice || limitPrice <= 0) {
        error = 'Enter both the stop trigger and stop limit.'; return;
      }
      if (choice.action === 'SELL' && limitPrice > triggerPrice) {
        error = 'A sell stop limit must be at or below its trigger.'; return;
      }
      if (choice.action === 'BUY' && limitPrice < triggerPrice) {
        error = 'A buy stop limit must be at or above its trigger.'; return;
      }
    } else if (choice.orderType === 'LIMIT' && (!price || price <= 0)) {
      error = 'Enter a positive limit price.'; return;
    }

    const immediate = choice.submission === 'immediate';
    if (immediate && !confirm(
      `${summary}\n\nSubmit this order now? Every policy and risk gate still applies, but if they pass this can place a REAL broker order.`
      + (persistentUntilFilled
        ? `\n\nThe unfilled remainder will be submitted again once per PSX trading day for up to ${expiresInDays} day(s).`
        : '')
    )) return;

    busy = true;
    try {
      if (immediate) {
        const placed = await trading.placeOrder({
          orderIntentId: choice.id,
          symbol: symbol.trim().toUpperCase(),
          quantity: submittedQuantity,
          price,
          triggerPrice,
          limitPrice,
          clientRequestId,
          persistentUntilFilled,
          expiresInDays
        });
        result = persistentUntilFilled && !placed.accepted && placed.persistentOrder?.state !== 'attention'
          ? {
              ok: true,
              title: 'Keep-working order saved',
              detail: `${placed.reason} It remains active for the next eligible trading day.`,
              executionId: placed.executionId || undefined
            }
          : placed.accepted
          ? { ok: true, title: 'Order accepted', detail: placed.reason, executionId: placed.executionId }
          : {
              ok: false,
              title: placed.reason.toLowerCase().includes('unknown')
                ? 'Broker outcome unknown — do not retry yet'
                : 'Order was not placed',
              detail: placed.reason,
              executionId: placed.executionId || undefined
            };
      } else {
        const request: ArmOrderRequest = {
          symbol: symbol.trim().toUpperCase(),
          action: choice.action,
          quantity: submittedQuantity,
          triggerKind: choice.triggerKind as TriggerKind,
          triggerPercent,
          referencePrice: currentPrice,
          trailing: choice.trailing,
          orderType: choice.orderType,
          price: choice.orderType === 'MARKET' ? null : conditionalLevel,
          expiresInDays,
          persistentUntilFilled,
          note: `New Order: ${choice.label}`
        };
        const armed = await trading.armed.arm(request);
        result = {
          ok: true,
          title: 'Waiting order armed',
          detail: `${armed.note} ${armed.willFireUnattended ? 'It can fire unattended.' : 'It still needs approval before sending.'}`
        };
      }
      dispatch('changed');
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
</script>

<!-- Order intent must not disappear because of a stray backdrop click or Escape press. The visible
     Close, Cancel and Done controls are the only dismissal paths. -->
<div class="backdrop" role="presentation">
  <div class="dialog" bind:this={dialogElement} role="dialog" aria-modal="true" aria-label="New order"
       tabindex="-1">
    <header>
      <div class="title"><ShoppingCart size={17} /><div><b>New Order</b><span>Choose what you want to happen</span></div></div>
      <button class="icon" on:click={() => dispatch('close')} aria-label="Close" disabled={busy}><X size={15} /></button>
    </header>

    {#if loading}
      <p class="loading">Loading order choices…</p>
    {:else if result}
      <div class="outcome" class:ok={result.ok}>
        {#if result.ok}<CheckCircle2 size={22} />{:else}<AlertTriangle size={22} />{/if}
        <div><b>{result.title}</b><p>{result.detail}</p>{#if result.executionId}<small>Execution {result.executionId}</small>{/if}</div>
      </div>
      <div class="footer"><button class="btn btn-primary" on:click={() => dispatch('close')}>Done</button></div>
    {:else}
      <div class="symbol-row">
        <label><span>Symbol</span><input list="new-order-symbols" bind:value={symbol} on:change={refreshQuote} /></label>
        <datalist id="new-order-symbols">{#each symbols as entry}<option value={entry.symbol}>{entry.companyName ?? ''}</option>{/each}</datalist>
        <div class="quote">
          <span>Latest price</span><b>{currentPrice != null ? money(currentPrice) : '—'}</b>
          <button class="icon" on:click={refreshQuote} disabled={quoteBusy} title="Refresh latest price"><RefreshCw size={13} /></button>
        </div>
      </div>

      <div class="choices">
        {#each categories as category}
          <section>
            <h3>{category}</h3>
            <div class="choice-grid">
              {#each registry?.intents.filter(item => item.category === category) ?? [] as item (item.id)}
                <button class="choice" class:selected={choice?.id === item.id} on:click={() => choose(item)}>
                  <b>{item.label}</b><span>{item.description}</span>
                  {#if item.submission === 'conditional'}<em><Clock3 size={11} /> waits for a trigger</em>{/if}
                </button>
              {/each}
            </div>
          </section>
        {/each}
      </div>

      {#if choice}
        <div class="form-card">
          <div class="form-head"><div><b>{choice.label}</b><span>{choice.action} · {choice.orderType}</span></div></div>
          {#if choice.action === 'BUY'}
            <section class="holding-card" aria-live="polite">
              <div class="holding-head">
                <div class="holding-title">
                  <Wallet size={15} />
                  <span>
                    <b>What you can spend</b>
                    <small>{holdingLoaded && account ? `Updated ${when(account.retrievedAtUtc)}` : 'Read buying power from the broker'}</small>
                  </span>
                </div>
                <button type="button" class="holding-refresh" on:click={loadHolding} disabled={holdingBusy}>
                  <span class:spin={holdingBusy}><RefreshCw size={13} /></span>
                  {holdingBusy ? 'Checking…' : holdingLoaded ? 'Refresh' : 'Check buying power'}
                </button>
              </div>

              {#if holdingError}
                <p class="holding-warning"><AlertTriangle size={13} /> {holdingError}</p>
              {/if}
              {#if holdingBusy && !account}
                <p class="holding-empty">Reading buying power from the broker…</p>
              {:else if !holdingLoaded}
                <p class="holding-empty">Check the broker to see what this account can commit right now.</p>
              {:else if account && !account.balancesAvailable}
                <p class="holding-warning"><AlertTriangle size={13} /> Balances are unavailable, so this is not being treated as an empty account.</p>
              {:else if buyingPower == null}
                <p class="holding-warning"><AlertTriangle size={13} /> The broker did not report a usable buying-power figure, so no limit is shown here. The order is still checked on submission.</p>
              {:else}
                <div class="holding-stats">
                  <div><span>{buyingPowerBalance?.label ?? 'Buying power'}</span><b>{money(buyingPower)} PKR</b></div>
                  {#each (account?.balances ?? []).filter(entry => entry.key !== 'available_cash' && entry.value != null) as entry}
                    <div><span>{entry.label}</span><b>{money(entry.value ?? 0)} PKR</b></div>
                  {/each}
                </div>
                <div class="holding-actions" aria-label="Quick order sizes">
                  <button type="button" on:click={() => buyFraction(.25)} disabled={Math.floor(buyingPower * .25) < 1}>25%</button>
                  <button type="button" on:click={() => buyFraction(.5)} disabled={Math.floor(buyingPower * .5) < 1}>50%</button>
                  <button type="button" on:click={() => buyFraction(1)} disabled={buyingPower < 1}>Use it all</button>
                </div>
                <p class="holding-note">
                  Buying power already accounts for what is committed to working orders. It is checked
                  again when the order reaches the broker.
                </p>
              {/if}
            </section>
          {/if}
          {#if choice.action === 'SELL'}
            <section class="holding-card" aria-live="polite">
              <div class="holding-head">
                <div class="holding-title">
                  <BriefcaseBusiness size={15} />
                  <span>
                    <b>Your {symbol.trim().toUpperCase() || 'selected'} holding</b>
                    <small>{holdingLoaded && account ? `Updated ${when(account.retrievedAtUtc)}` : 'Read holdings and working orders from the broker'}</small>
                  </span>
                </div>
                <button type="button" class="holding-refresh" on:click={loadHolding} disabled={holdingBusy}>
                  <span class:spin={holdingBusy}><RefreshCw size={13} /></span>
                  {holdingBusy ? 'Checking…' : holdingLoaded ? 'Refresh' : 'Check holding'}
                </button>
              </div>

              {#if holdingError}
                <p class="holding-warning"><AlertTriangle size={13} /> {holdingError}</p>
              {/if}
              {#if holdingBusy && !account}
                <p class="holding-empty">Reading current holdings and working orders…</p>
              {:else if !holdingLoaded}
                <p class="holding-empty">Check the broker to see what is owned, already committed, and available to sell.</p>
              {:else if account && !account.holdingsAvailable}
                <p class="holding-warning"><AlertTriangle size={13} /> Holdings are unavailable, so this is not being treated as an empty position.</p>
              {:else if account}
                <div class="holding-stats">
                  <div><span>Owned</span><b>{heldQuantity == null ? 'Unknown' : `${money(heldQuantity)} shares`}</b></div>
                  <div><span>In working SELLs</span><b>{committedSellQuantity == null ? 'Unknown' : `${money(committedSellQuantity)} shares`}</b></div>
                  <div><span>Available now</span><b>{availableSellQuantity == null ? 'Unknown' : `${money(availableSellQuantity)} shares`}</b></div>
                </div>

                {#if !account.ordersAvailable}
                  <p class="holding-warning"><AlertTriangle size={13} /> The working-order book is unavailable. Sell-all shortcuts stay disabled rather than assuming no shares are committed.</p>
                {:else if matchingSellOrders.some(order => order.remainingQuantity == null)}
                  <p class="holding-warning"><AlertTriangle size={13} /> A working SELL has an unknown remaining quantity, so available shares cannot be calculated safely.</p>
                {:else if availableSellQuantity != null}
                  <div class="holding-actions" aria-label="Quick sell quantities">
                    <button type="button" on:click={() => sellFraction(.25)} disabled={Math.floor(availableSellQuantity * .25) < 1}>25%</button>
                    <button type="button" on:click={() => sellFraction(.5)} disabled={Math.floor(availableSellQuantity * .5) < 1}>50%</button>
                    <button type="button" class="sell-all" on:click={() => sellFraction(1)} disabled={availableSellQuantity < 1}>Sell all available</button>
                  </div>
                  <p class="holding-note">
                    {#if availableSellValue != null}Available value at this order price is about <b>{money(availableSellValue)} PKR</b>. {/if}
                    Availability is checked again when the order reaches the broker.
                  </p>
                {/if}
              {/if}
            </section>
          {/if}
          <div class="grid">
            <div class="size-control">
              <span class="field-label">Order size</span>
              <div class="size-tabs" role="group" aria-label="Choose how to size the order">
                <button type="button" class:active={sizeMode === 'shares'} aria-pressed={sizeMode === 'shares'}
                        on:click={() => sizeMode = 'shares'}>Shares</button>
                <button type="button" class:active={sizeMode === 'value'} aria-pressed={sizeMode === 'value'}
                        on:click={() => sizeMode = 'value'}>Value (PKR)</button>
              </div>
              {#if sizeMode === 'shares'}
                <label><span>Quantity (shares)</span><input type="number" min="1" step="1" bind:value={quantity} /></label>
              {:else}
                <label><span>Order value (PKR)</span><input type="number" min="1" step="1" bind:value={orderValue} /></label>
              {/if}
            </div>
            {#if choice.priceField === 'limit' || choice.priceField === 'target'}
              <label><span>{choice.priceField === 'target' ? 'Target sell price' : 'Limit price'}</span><input type="number" min="0.01" step="0.01" bind:value={price} /></label>
            {/if}
            {#if choice.priceField === 'stop'}
              <label><span>Trigger price</span><input type="number" min="0.01" step="0.01" bind:value={triggerPrice} /></label>
              <label><span>Worst acceptable price after trigger</span><input type="number" min="0.01" step="0.01" bind:value={limitPrice} /></label>
            {/if}
            {#if choice.submission === 'conditional'}
              <label><span>Move from {currentPrice ?? 'latest price'} (%)</span><input type="number" min="0.1" max="50" step="0.1" bind:value={triggerPercent} /></label>
            {/if}
            {#if choice.submission === 'conditional' || persistentUntilFilled}
              <label><span>Expires in (days)</span><input type="number" min="1" max="365" bind:value={expiresInDays} /></label>
            {/if}
          </div>

          {#if persistable}
            <label class="persist-check">
              <input type="checkbox" bind:checked={persistentUntilFilled} />
              <span>
                <b>Keep the unfilled remainder working</b>
                <em>Re-place it once per PSX trading day until fully filled or expired.</em>
              </span>
            </label>
            {#if persistentUntilFilled}
              <p class="warning">
                Each day is a new real broker order and must pass the current approval, holdings,
                reconciliation, kill-switch, and risk limits. The price you entered will not be
                changed to a worse price merely to fit that day&#39;s trading band.
              </p>
            {/if}
          {/if}

          {#if marketDisabled}
            <p class="warning"><AlertTriangle size={13} /> Market orders are disabled in broker settings. Choose a limit-price option or enable them deliberately.</p>
          {/if}
          {#if sizeMode === 'value' && valueSizedQuantity && estimatedPrice && estimatedValue != null && sizingDifference != null}
            <p class="sizing-note">
              Nearest whole-share quantity: <b>{valueSizedQuantity} shares</b> at {money(estimatedPrice)} PKR
              = {money(estimatedValue)} PKR
              ({sizingDifference === 0 ? 'exactly your value' : `${money(Math.abs(sizingDifference))} PKR ${sizingDifference > 0 ? 'above' : 'below'} your value`}).
            </p>
          {/if}
          {#if buyExceedsBuyingPower && estimatedValue != null && buyingPower != null}
            <p class="warning"><AlertTriangle size={13} /> This order is about {money(estimatedValue)} PKR, above the {money(buyingPower)} PKR the broker currently reports as available. It is not blocked here — the broker decides — but it may be refused or reduced.</p>
          {/if}
          {#if sellExceedsAvailable && effectiveQuantity != null && availableSellQuantity != null}
            <p class="warning"><AlertTriangle size={13} /> You entered {money(effectiveQuantity)} shares, but the broker snapshot shows only {money(availableSellQuantity)} available now. Use “Sell all available” or reduce the size; final availability is checked again on submission.</p>
          {/if}
          {#if summary}<p class="summary">{summary}</p>{/if}
          {#if estimatedValue}<p class="estimate">Estimated value: <b>{money(estimatedValue)} PKR</b>{choice.orderType === 'MARKET' ? ' at the latest price; actual value can move.' : ''}</p>{/if}
          <!--
            Extension point for whoever is hosting this dialog. Deliberately generic: it passes the
            order being composed and takes no view on what, if anything, is rendered — this repo models
            no fee schedule, and a host that does (a broker integration, an edition with a cost model)
            can show one here without this component learning about it. Renders nothing when unfilled.
          -->
          <slot name="order-detail"
                symbol={selectedTicker}
                action={choice.action}
                orderType={choice.orderType}
                quantity={effectiveQuantity}
                price={estimatedPrice}
                value={estimatedValue} />
        </div>
      {/if}

      {#if error}<p class="error"><AlertTriangle size={13} /> {error}</p>{/if}
      <div class="footer">
        <button class="btn btn-ghost" on:click={() => dispatch('close')} disabled={busy}>Cancel</button>
        <button class="btn btn-primary" on:click={submit} disabled={!choice || busy || marketDisabled}>
          {busy ? 'Submitting…' : choice?.submission === 'conditional' ? 'Arm waiting order' : 'Review & submit'}
        </button>
      </div>
    {/if}
  </div>
</div>

<style>
  .backdrop { position:fixed; inset:0; z-index:1000; background:rgba(0,0,0,.68); display:flex;
              align-items:center; justify-content:center; padding:1rem; }
  .dialog { width:min(920px, 96vw); max-height:92vh; overflow:auto; background:var(--surface);
            border:1px solid var(--border-md); border-radius:var(--radius); box-shadow:0 20px 70px rgba(0,0,0,.5); }
  header { position:sticky; top:0; z-index:2; display:flex; justify-content:space-between; align-items:center;
           padding:.9rem 1rem; background:var(--surface); border-bottom:1px solid var(--border); }
  .title { display:flex; align-items:center; gap:.55rem; color:var(--primary); }
  .title div { display:flex; flex-direction:column; gap:.12rem; }.title b { color:var(--text); font-size:.96rem; }
  .title span { color:var(--text-3); font-size:.68rem; }
  .icon { border:0; background:none; color:var(--text-3); cursor:pointer; padding:.3rem; display:flex; border-radius:var(--radius-sm); }
  .icon:hover { background:var(--surface-3); color:var(--text); }.icon:disabled { opacity:.5; cursor:wait; }
  .loading { padding:2rem; color:var(--text-3); text-align:center; }
  .symbol-row { display:flex; align-items:end; gap:1rem; padding:1rem; border-bottom:1px solid var(--border); flex-wrap:wrap; }
  label { display:flex; flex-direction:column; gap:.3rem; color:var(--text-3); font-size:.68rem; }
  .persist-check { margin-top:.8rem; flex-direction:row; align-items:flex-start; gap:.55rem;
                   padding:.7rem; border:1px solid var(--border); border-radius:var(--radius-sm);
                   background:var(--surface-2); }
  .persist-check input { min-width:auto; margin-top:.15rem; }
  .persist-check span { display:flex; flex-direction:column; gap:.15rem; }
  .persist-check b { color:var(--text); font-size:.75rem; }
  .persist-check em { color:var(--text-3); font-style:normal; }
  input { background:var(--surface-2); border:1px solid var(--border-md); border-radius:var(--radius-sm);
          color:var(--text); padding:.5rem .6rem; font:inherit; min-width:150px; }
  .quote { display:grid; grid-template-columns:auto auto auto; align-items:center; gap:.45rem; color:var(--text-3); font-size:.68rem; }
  .quote b { color:var(--text); font-size:.85rem; }
  .choices { padding:.3rem 1rem .8rem; }.choices section { margin-top:.8rem; }
  h3 { margin:0 0 .4rem; color:var(--text-2); font-size:.7rem; text-transform:uppercase; letter-spacing:.06em; }
  .choice-grid { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:.45rem; }
  .choice { min-height:88px; text-align:left; display:flex; flex-direction:column; gap:.3rem; border:1px solid var(--border);
            background:var(--surface-2); color:var(--text); border-radius:var(--radius-sm); padding:.65rem; cursor:pointer; }
  .choice:hover { border-color:var(--border-md); background:var(--surface-3); }
  .choice.selected { border-color:var(--primary); box-shadow:inset 0 0 0 1px var(--primary); }
  .choice b { font-size:.75rem; }.choice span { color:var(--text-3); font-size:.65rem; line-height:1.4; }
  .choice em { color:var(--warning); font-size:.61rem; display:flex; align-items:center; gap:.2rem; margin-top:auto; font-style:normal; }
  .form-card { margin:0 1rem 1rem; padding:.8rem; background:var(--surface-2); border:1px solid var(--border-md); border-radius:var(--radius-sm); }
  .form-head { display:flex; justify-content:space-between; margin-bottom:.65rem; }.form-head div { display:flex; align-items:center; gap:.5rem; }
  .form-head b { font-size:.82rem; }.form-head span { color:var(--text-3); font-size:.65rem; }
  .holding-card { margin-bottom:.75rem; padding:.7rem; border:1px solid var(--border); border-radius:var(--radius-sm);
                  background:var(--surface); }
  .holding-head { display:flex; align-items:center; justify-content:space-between; gap:.7rem; }
  .holding-title { display:flex; align-items:center; gap:.45rem; color:var(--primary); min-width:0; }
  .holding-title span { display:flex; flex-direction:column; gap:.12rem; min-width:0; }
  .holding-title b { color:var(--text); font-size:.75rem; }
  .holding-title small { color:var(--text-3); font-size:.62rem; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .holding-refresh { display:flex; align-items:center; gap:.3rem; flex:none; border:1px solid var(--border-md);
                     border-radius:var(--radius-sm); padding:.35rem .5rem; background:var(--surface-2);
                     color:var(--text-2); font:inherit; font-size:.65rem; cursor:pointer; }
  .holding-refresh:hover { color:var(--text); border-color:var(--border-hover); }
  .holding-refresh:disabled { opacity:.6; cursor:wait; }
  .spin { display:flex; animation:spin 1s linear infinite; }
  @keyframes spin { to { transform:rotate(360deg); } }
  .holding-empty { margin:.6rem 0 0; color:var(--text-3); font-size:.67rem; line-height:1.45; }
  .holding-warning { margin:.6rem 0 0; display:flex; align-items:flex-start; gap:.35rem; color:var(--warning);
                     font-size:.67rem; line-height:1.45; }
  .holding-warning :global(svg) { flex:none; margin-top:.1rem; }
  .holding-stats { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:.45rem; margin-top:.65rem; }
  .holding-stats div { display:flex; flex-direction:column; gap:.18rem; padding:.5rem .55rem;
                       border:1px solid var(--border); border-radius:var(--radius-sm); background:var(--surface-2); }
  .holding-stats span { color:var(--text-3); font-size:.61rem; }
  .holding-stats b { color:var(--text); font-size:.72rem; }
  .holding-actions { display:flex; flex-wrap:wrap; gap:.35rem; margin-top:.6rem; }
  .holding-actions button { border:1px solid var(--border-md); border-radius:var(--radius-sm); padding:.35rem .55rem;
                            background:var(--surface-2); color:var(--text-2); font:inherit; font-size:.65rem; cursor:pointer; }
  .holding-actions button:hover:not(:disabled) { color:var(--text); border-color:var(--primary); }
  .holding-actions button:focus-visible,.holding-refresh:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .holding-actions button:disabled { opacity:.45; cursor:not-allowed; }
  .holding-actions .sell-all { color:var(--danger); border-color:color-mix(in srgb,var(--danger) 40%,var(--border)); }
  .holding-note { margin:.45rem 0 0; color:var(--text-3); font-size:.63rem; line-height:1.45; }
  .holding-note b { color:var(--text-2); }
  .grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:.6rem; }.grid input { width:100%; box-sizing:border-box; }
  .size-control { display:flex; flex-direction:column; gap:.4rem; }
  .field-label { color:var(--text-3); font-size:.68rem; }
  .size-tabs { align-self:flex-start; display:flex; padding:2px; border:1px solid var(--border-md);
               border-radius:var(--radius-sm); background:var(--surface); }
  .size-tabs button { border:0; border-radius:calc(var(--radius-sm) - 2px); padding:.3rem .55rem;
                      background:transparent; color:var(--text-3); font:inherit; font-size:.66rem; cursor:pointer; }
  .size-tabs button:hover { color:var(--text); background:var(--surface-3); }
  .size-tabs button.active { color:var(--text); background:var(--surface-3); box-shadow:0 0 0 1px var(--border-md); }
  .size-tabs button:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .sizing-note { margin:.7rem 0 0; padding:.5rem .6rem; border-left:2px solid var(--primary);
                 background:color-mix(in srgb,var(--primary) 7%,transparent); color:var(--text-2);
                 font-size:.7rem; line-height:1.45; }
  .sizing-note b { color:var(--text); }
  .summary { margin:.7rem 0 0; color:var(--text); font-size:.75rem; line-height:1.5; font-weight:600; }
  .estimate { margin:.3rem 0 0; color:var(--text-3); font-size:.68rem; }.estimate b { color:var(--text-2); }
  .warning,.error { margin:.65rem 1rem; padding:.55rem .65rem; border-radius:var(--radius-sm); display:flex; gap:.35rem;
                    align-items:flex-start; color:var(--warning); background:color-mix(in srgb,var(--warning) 8%,transparent); font-size:.7rem; line-height:1.4; }
  .form-card .warning { margin:.65rem 0 0; }.error { color:var(--danger); background:color-mix(in srgb,var(--danger) 8%,transparent); }
  .footer { display:flex; justify-content:flex-end; gap:.5rem; padding:.8rem 1rem; border-top:1px solid var(--border); }
  .outcome { margin:1rem; padding:1rem; display:flex; gap:.7rem; border:1px solid color-mix(in srgb,var(--danger) 35%,transparent);
             background:color-mix(in srgb,var(--danger) 7%,transparent); color:var(--danger); border-radius:var(--radius-sm); }
  .outcome.ok { color:var(--success); border-color:color-mix(in srgb,var(--success) 35%,transparent); background:color-mix(in srgb,var(--success) 7%,transparent); }
  .outcome div { display:flex; flex-direction:column; gap:.3rem; }.outcome p { margin:0; color:var(--text-2); font-size:.75rem; line-height:1.5; }.outcome small { color:var(--text-3); }
  @media(max-width:760px) { .choice-grid { grid-template-columns:repeat(2,minmax(0,1fr)); } }
  @media(max-width:480px) {
    .choice-grid,.grid,.holding-stats { grid-template-columns:1fr; }
    .holding-head { align-items:flex-start; }
    .holding-refresh { padding:.35rem; }
  }
</style>
