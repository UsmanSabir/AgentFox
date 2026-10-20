import { getContext, setContext } from 'svelte';
import type { PortfolioCosts } from './portfolioAllocation';

/** A read-only cost source. The chart knows no broker, endpoint or accounting implementation.
 * Called only after values are revealed and on a new account snapshot; it stores nothing itself. */
export type PortfolioCostReader = () => Promise<PortfolioCosts>;
const KEY = Symbol('trading-portfolio-costs');

export function providePortfolioCosts(read: PortfolioCostReader): void { setContext(KEY, read); }
export function usePortfolioCosts(): PortfolioCostReader | null { return getContext(KEY) ?? null; }
