/**
 * The one extension seam the trading dashboard offers a plugin that manages symbols.
 *
 * WHY THIS EXISTS. An edition built on top of this dashboard — or any future plugin — needs to say
 * something per symbol: a status beside a watchlist row, a status beside a holding, and a fuller view
 * of what it intends for the selected symbol. The alternatives are all worse. Copying the panels
 * forks them. Adding edition-specific fields to `api.ts` teaches the community build about a product
 * it does not ship. Re-mapping routes is already forbidden on the server for the same reason.
 *
 * WHAT IT IS NOT. It carries no vocabulary from any edition: no strategies, no campaigns, no plans.
 * The dashboard renders whatever component it is handed, passes it a symbol, and knows nothing else.
 * A community build passes nothing and is byte-for-byte unchanged on screen.
 *
 * THE CONTRACT. Every component here receives a `symbol` prop. `plan` additionally receives
 * `companyName` and, in the docked workspace, `allowCollapse={false}`: the tab already owns visibility,
 * so components with a disclosure should keep their content open. The stacked layout leaves this
 * optional prop at the component's default. Components must render nothing when they have nothing
 * to say, because they sit inside layouts this repo owns and an empty box is a visual defect.
 */

/**
 * A Svelte component constructor.
 *
 * Deliberately loose. Pinning this to one Svelte component-type API would couple the extension
 * contract to a major version of Svelte, and the whole point of the seam is that the two editions can
 * be built and released independently.
 */
export type SymbolExtensionComponent = any;

export type SymbolExtension = {
  /** Rendered inside each watchlist row, next to the existing tags. */
  rowStatus?: SymbolExtensionComponent | null;

  /** Rendered inside each portfolio holding row, under the instrument name. */
  holdingStatus?: SymbolExtensionComponent | null;

  /** Rendered in a docked tab on desktop or beneath the chart in the stacked workspace. */
  plan?: SymbolExtensionComponent | null;
};
