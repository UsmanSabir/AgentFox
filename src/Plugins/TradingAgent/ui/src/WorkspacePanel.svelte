<script lang="ts">
  import type { WorkspaceComposition } from './workspaceComposition';
  export let workspace: WorkspaceComposition | null = null;
  export let id: string;
  let visible = workspace === null;
  function attach(node: HTMLElement) {
    let layoutVisible = true;
    let intersects = workspace === null;
    const update = () => visible = layoutVisible && intersects;
    const detach = workspace?.attachPanel(id, node, shown => { layoutVisible = shown; update(); });
    // Dockview keeps producer instances mounted in hidden tabs and drawers. Visibility lets an
    // explicitly opened panel start its reader without loading every hidden account panel at entry.
    const observer = workspace ? new IntersectionObserver(([entry]) => {
      intersects = entry.isIntersecting && entry.intersectionRect.width > 0 && entry.intersectionRect.height > 0;
      update();
    }) : null;
    observer?.observe(node);
    return { destroy: () => { observer?.disconnect(); detach?.(); } };
  }
</script>
<div use:attach class="workspace-surface" class:hosted={workspace !== null}><slot {visible}/></div>
<style>
  .workspace-surface { display:contents; }
  .workspace-surface.hosted { display:block; min-width:0; min-height:0; }
</style>
