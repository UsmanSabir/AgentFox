/** Calendar dates for scheduled orders always belong to the exchange, not the browser. */
export function psxDateInput(now = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: 'Asia/Karachi', year: 'numeric', month: '2-digit', day: '2-digit'
  }).formatToParts(now);
  const part = (type: string) => parts.find(p => p.type === type)!.value;
  return `${part('year')}-${part('month')}-${part('day')}`;
}

export function scheduleDateError(value: string, now = new Date()): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return 'Choose an activation date in Pakistan time (PKT).';
  const date = new Date(`${value}T00:00:00Z`);
  if (!Number.isFinite(date.getTime()) || date.toISOString().slice(0, 10) !== value)
    return 'Choose a valid activation date.';
  return value < psxDateInput(now) ? 'The activation date has already passed in Pakistan (PKT).' : null;
}

export const describePsxDate = (value: string) => new Date(value).toLocaleDateString(undefined, {
  timeZone: 'Asia/Karachi', year: 'numeric', month: 'short', day: 'numeric'
}) + ' PKT';
