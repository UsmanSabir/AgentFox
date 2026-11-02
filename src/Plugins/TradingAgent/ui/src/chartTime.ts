import { TickMarkType, type Time } from 'lightweight-charts';

/** Format exchange time without changing the UTC keys shared by candles and overlays. */
export function chartTimeFormatters(intraday: boolean) {
  const options = { timeZone: intraday ? 'Asia/Karachi' : 'UTC' };
  const format = (fields: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat('en-GB', { ...options, ...fields });
  const year = format({ year: 'numeric' });
  const month = format({ month: 'short' });
  const day = format({ day: '2-digit' });
  const clock = format({ hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const seconds = format({ hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
  const date = format({ day: '2-digit', month: 'short', year: 'numeric' });
  // Business days are session dates, not instants: never shift their calendar date.
  const businessDate = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'UTC', day: '2-digit', month: 'short', year: 'numeric'
  });
  const asDate = (time: Time) => typeof time === 'number' ? new Date(time * 1000)
    : typeof time === 'string' ? new Date(`${time}T00:00:00Z`)
    : new Date(Date.UTC(time.year, time.month - 1, time.day));

  return {
    tickMarkFormatter(time: Time, type: TickMarkType): string | null {
      // Let the library format date-only values; they have no time zone to convert.
      if (typeof time !== 'number') return null;
      const value = asDate(time);
      switch (type) {
        case TickMarkType.Year: return year.format(value);
        case TickMarkType.Month: return month.format(value);
        case TickMarkType.DayOfMonth: return day.format(value);
        case TickMarkType.Time: return clock.format(value);
        case TickMarkType.TimeWithSeconds: return seconds.format(value);
        default: return null;
      }
    },
    timeFormatter(time: Time): string {
      const value = asDate(time);
      if (typeof time !== 'number') return businessDate.format(value);
      return intraday ? `${date.format(value)} ${clock.format(value)} PKT` : date.format(value);
    }
  };
}
