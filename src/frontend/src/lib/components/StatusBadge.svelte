<script lang="ts">
  export let status: string = 'unknown';

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

  $: cls = map[status.toLowerCase()] ?? 'badge-neutral';

  const dotColor: Record<string, string> = {
    'badge-success': 'var(--success)',
    'badge-primary': 'var(--primary)',
    'badge-warning': 'var(--warning)',
    'badge-danger':  'var(--danger)',
    'badge-neutral': 'var(--text-3)',
  };
  $: dot = dotColor[cls] ?? 'var(--text-3)';
</script>

<span class="badge {cls}">
  <span class="pulse-dot" style="background:{dot};"></span>
  {status}
</span>
