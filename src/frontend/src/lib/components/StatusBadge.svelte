<script lang="ts">
  interface Props {
    status?: string;
  }

  let { status = 'unknown' }: Props = $props();

  const map: Record<string, string> = {
    idle:         'badge-success',
    active:       'badge-primary',
    running:      'badge-primary',
    thinking:     'badge-warning',
    error:        'badge-danger',
    initializing: 'badge-neutral',
    ready:        'badge-success',
    unknown:      'badge-neutral',
  };

  let cls = $derived(map[status.toLowerCase()] ?? 'badge-neutral');

  const dotColor: Record<string, string> = {
    'badge-success': 'var(--success)',
    'badge-primary': 'var(--primary)',
    'badge-warning': 'var(--warning)',
    'badge-danger':  'var(--danger)',
    'badge-neutral': 'var(--text-3)',
  };
  let dot = $derived(dotColor[cls] ?? 'var(--text-3)');
</script>

<span class="badge {cls}">
  <span class="pulse-dot" style="background:{dot};"></span>
  {status}
</span>
