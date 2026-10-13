import type { BrokerAccountHolding } from './api';
import type { LivePriceView } from './livePrices';

export type PortfolioPnlTone = 'gain' | 'loss' | 'flat' | 'unknown';

export interface PortfolioPnlSummary {
  amount: number | null;
  percent: number | null;
  currency: string | null;
  tone: PortfolioPnlTone;
  unavailableCount: number;
  liveMarkCount: number;
  mixedCurrencies: boolean;
}

const finite = (value: number | null | undefined): value is number =>
  typeof value === 'number' && Number.isFinite(value);

const currencyOf = (holding: BrokerAccountHolding) =>
  holding.currency?.trim().toUpperCase() || 'PKR';

/**
 * Add the unrealized result for every current holding without turning missing broker data into zero.
 * A live mark follows the same arithmetic as LiveHoldingCells; otherwise the broker's own P/L wins.
 * Cost basis is only required for the percentage, so a reliable amount can still be shown when its
 * percentage cannot.
 */
export function summarizePortfolioPnl(
  holdings: BrokerAccountHolding[],
  views: LivePriceView[] = []
): PortfolioPnlSummary {
  let amount = 0;
  let cost = 0;
  let unavailableCount = 0;
  let costUnavailableCount = 0;
  let liveMarkCount = 0;
  const currencies = new Set<string>();

  holdings.forEach((holding, index) => {
    const mark = views[index]?.quote?.current;
    const quantity = holding.quantity;
    const averageCost = holding.averageCost;
    let profitLoss = holding.unrealizedProfitLoss;
    let usedLiveMark = false;
    if (finite(mark) && finite(quantity) && finite(averageCost)) {
      profitLoss = (mark - averageCost) * quantity;
      usedLiveMark = true;
    }

    if (!finite(profitLoss)) {
      unavailableCount += 1;
      return;
    }

    amount += profitLoss;
    currencies.add(currencyOf(holding));
    if (usedLiveMark) liveMarkCount += 1;

    const costValue = finite(holding.costValue) && holding.costValue >= 0
      ? holding.costValue
      : finite(quantity) && finite(averageCost)
        ? Math.abs(quantity * averageCost)
        : null;
    if (costValue == null) costUnavailableCount += 1;
    else cost += costValue;
  });

  const mixedCurrencies = currencies.size > 1;
  const complete = holdings.length > 0 && unavailableCount === 0 && !mixedCurrencies;
  const resolvedAmount = complete ? amount : null;
  const percent = complete && costUnavailableCount === 0 && cost > 0
    ? amount / cost * 100
    : null;
  const tone: PortfolioPnlTone = resolvedAmount == null
    ? 'unknown'
    : resolvedAmount > 0
      ? 'gain'
      : resolvedAmount < 0
        ? 'loss'
        : 'flat';

  return {
    amount: resolvedAmount,
    percent,
    currency: currencies.size === 1 ? [...currencies][0] : null,
    tone,
    unavailableCount,
    liveMarkCount,
    mixedCurrencies
  };
}
