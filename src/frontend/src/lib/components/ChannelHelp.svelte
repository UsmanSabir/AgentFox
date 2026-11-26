<script lang="ts">
  interface Props {
    label: string;
    text: string;
  }

  let { label, text }: Props = $props();
</script>

<details class="help">
  <summary aria-label={`Help about ${label}`}>?</summary>
  <div class="help-content">{text}</div>
</details>

<style>
  .help {
    position: relative;
    display: inline-block;
    vertical-align: middle;
  }
  summary {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    list-style: none;
    border: 1px solid var(--border-md);
    border-radius: 50%;
    color: var(--text-2);
    background: var(--surface);
    cursor: pointer;
    font-size: 0.75rem;
    font-weight: 500;
  }
  summary::-webkit-details-marker {
    display: none;
  }
  summary:hover {
    color: var(--text);
    border-color: var(--primary);
  }
  .help-content {
    position: absolute;
    z-index: 10;
    inset-inline-end: 0;
    top: calc(100% + 6px);
    width: min(270px, calc(100vw - 64px));
    padding: 12px;
    border: 1px solid var(--border-md);
    border-radius: 8px;
    background: var(--surface-2);
    color: var(--text);
    font-size: 0.8125rem;
    font-weight: 400;
    line-height: 1.6;
    box-shadow: 0 8px 24px #0003;
  }
  summary:focus-visible {
    outline: 2px solid var(--primary);
    outline-offset: 3px;
  }
  @media (pointer: coarse) {
    summary {
      width: 44px;
      height: 44px;
    }
  }
</style>
