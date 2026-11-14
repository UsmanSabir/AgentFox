/**
 * The conditional-order dialog's one-sentence read-back, as a pure function so every wording is
 * tested without mounting the dialog.
 *
 * A TRAILING percent trigger is not "N% from the starting price": the trigger follows the best price
 * seen since the order was saved (a SELL's highest, a BUY's lowest) and the percent is measured from
 * THAT. So a trailing sentence names the direction it follows and never quotes a fixed trigger level
 * once a price bound is set, because a fixed number next to a bound reads as a contradiction (a BUY
 * that "rises to 746.64" with a maximum of 671.68) when the real trigger moves with the extreme.
 */
export interface ArmSummaryInput {
  action: 'BUY' | 'SELL';
  quantity: number | null;
  symbol: string;
  orderType: string;
  /** The order's own price (limit, or a stop's trigger); null when none is set. */
  price: number | null;
  isEvent: boolean;
  triggerAlertKind: string | null;
  isPercent: boolean;
  /** True when the trigger falls to fire (PriceBelow / PercentDrop). */
  fallsToTrigger: boolean;
  /** The level the order fires at when first armed; null when unknown. */
  level: number | null;
  triggerPercent: number | null;
  referencePrice: number | null;
  trailing: boolean;
  /** The trailing LIMIT's maximum buy / minimum sell price, only when it applies. */
  priceBound: number | null;
}

const money = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 2 });

export function armOrderSummary(i: ArmSummaryInput): string | null {
  if (!i.quantity || i.quantity <= 0) return null;
  const fill = i.orderType === 'MARKET'
    ? 'at the best price available'
    : i.price != null ? `at ${money(i.price)}` : 'with no price set';
  const verb = i.action === 'SELL' ? 'Sells' : 'Buys';

  if (i.isEvent) {
    return i.triggerAlertKind
      ? `${verb} ${i.quantity} ${i.symbol} ${fill} when ${i.symbol} raises `
        + `"${i.triggerAlertKind.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()}".`
      : null;
  }

  if (i.level == null) return null;
  const move = i.fallsToTrigger ? 'falls' : 'rises';

  if (!i.isPercent) return `${verb} ${i.quantity} ${i.symbol} ${fill} if it ${move} to ${money(i.level)}.`;

  const from = i.referencePrice != null ? money(i.referencePrice) : '—';

  if (!i.trailing) {
    return `${verb} ${i.quantity} ${i.symbol} ${fill} if it ${move} ${i.triggerPercent}% `
         + `from ${from} — that is ${money(i.level)}.`;
  }

  // Trailing: a falling trigger (a stop) follows the price UP and measures from its high; a rising
  // trigger (a rebound buy) follows the price DOWN and measures from its low.
  const follows = i.fallsToTrigger ? 'up' : 'down';
  const extreme = i.fallsToTrigger ? 'highest' : 'lowest';
  const head = `Follows ${i.symbol} ${follows} from ${from} and ${verb.toLowerCase()} ${i.quantity} ${i.symbol} ${fill} `
    + `once it ${move} ${i.triggerPercent}% from its ${extreme} point`;
  if (i.priceBound != null && i.priceBound > 0) {
    return `${head}, at no ${i.fallsToTrigger ? 'less' : 'more'} than ${money(i.priceBound)}.`;
  }
  return `${head} — that is ${money(i.level)} to begin with.`;
}

/**
 * The short line ABOVE the summary: where the order fires. For a fixed trigger it is one level
 * ("fires"). For a TRAILING trigger there is no fixed level once the trigger follows the price, so
 * the line describes the reference honestly ("trails") instead of quoting a number that contradicts
 * a price bound — "Fires at 746.64" next to a 671.68 maximum on a BUY.
 */
export type ArmLevelLine =
  | { kind: 'fires'; level: number }
  | { kind: 'trails'; text: string };

export function armLevelLine(i: ArmSummaryInput): ArmLevelLine | null {
  if (i.isEvent || i.level == null) return null;
  if (!i.isPercent || !i.trailing) return { kind: 'fires', level: i.level };
  const from = i.referencePrice != null ? money(i.referencePrice) : '—';
  const extreme = i.fallsToTrigger ? 'highest' : 'lowest';
  const extremeNoun = i.fallsToTrigger ? 'high' : 'low';
  const move = i.fallsToTrigger ? 'fall' : 'rise';
  const head = `Trails the ${extreme} price from ${from} · fires on a ${i.triggerPercent}% ${move} from that ${extremeNoun}`;
  if (i.priceBound != null && i.priceBound > 0) {
    return { kind: 'trails', text: `${head}, at no ${i.fallsToTrigger ? 'less' : 'more'} than ${money(i.priceBound)}` };
  }
  return { kind: 'trails', text: `${head} · starts at ${money(i.level)}` };
}
