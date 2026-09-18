const ones = ['zero', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine',
  'ten', 'eleven', 'twelve', 'thirteen', 'fourteen', 'fifteen', 'sixteen', 'seventeen', 'eighteen', 'nineteen'];
const tens = ['', '', 'twenty', 'thirty', 'forty', 'fifty', 'sixty', 'seventy', 'eighty', 'ninety'];

function integerWords(value: number): string {
  if (value < 20) return ones[value];
  if (value < 100) return tens[Math.floor(value / 10)] + (value % 10 ? `-${ones[value % 10]}` : '');
  for (const [scale, name] of [[1e12, 'trillion'], [1e9, 'billion'], [1e6, 'million'],
    [1e3, 'thousand'], [100, 'hundred']] as const) {
    if (value >= scale) return `${integerWords(Math.floor(value / scale))} ${name}`
      + (value % scale ? ` ${integerWords(value % scale)}` : '');
  }
  return '';
}

/** Display-only PKR readback, rounded to paisa. Never used to size or submit an order. */
export function amountInWords(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value) || value < 0) return '';
  const totalPaisa = Math.round(value * 100);
  if (!Number.isSafeInteger(totalPaisa)) return '';
  const rupees = Math.floor(totalPaisa / 100);
  const paisa = totalPaisa % 100;
  const words = `${integerWords(rupees)} ${rupees === 1 ? 'rupee' : 'rupees'}`
    + (paisa ? ` and ${integerWords(paisa)} paisa` : '');
  return words[0].toUpperCase() + words.slice(1);
}
