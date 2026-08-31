import type { ComponentType } from 'svelte';

/** A neutral section link so an edition can extend the dashboard without the core knowing why. */
export interface SectionNavigationItem {
  id: string;
  label: string;
  icon: ComponentType;
}
