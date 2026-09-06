<script lang="ts">
  import type { WorkspaceComposition } from './workspaceComposition';
  export let workspace: WorkspaceComposition | null = null;
  export let id: string;
  function attach(node: HTMLElement) {
    const detach = workspace?.attachPanel(id, node);
    return { destroy: () => detach?.() };
  }
</script>
<div use:attach class="workspace-surface" class:hosted={workspace !== null}><slot /></div>
<style>
  .workspace-surface { display:contents; }
  .workspace-surface.hosted { display:block; min-width:0; min-height:0; }
</style>
