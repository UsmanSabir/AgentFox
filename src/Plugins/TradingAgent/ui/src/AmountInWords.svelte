<script lang="ts">
  import { amountInWords } from './amountInWords';
  interface Props {
    value: number | null | undefined;
  }

  let { value }: Props = $props();
  let words = $derived(amountInWords(value));
</script>

{#if words}<small class="amount-words">{words}</small>{/if}

<style>
  .amount-words {
    display: block;
    color: var(--text-2);
    font-size: .75rem;
    font-weight: 400;
    line-height: 1.5;
    text-transform: none;
    letter-spacing: normal;
    overflow-wrap: anywhere;
  }
</style>
