const rtf = new Intl.RelativeTimeFormat("en", { numeric: "always" });
const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 365 * 24 * 3600],
  ["month", 30 * 24 * 3600],
  ["week", 7 * 24 * 3600],
  ["day", 24 * 3600],
  ["hour", 3600],
  ["minute", 60],
];

/** "just now", "5 minutes ago", "1 day ago"; "—" for a missing or unparseable time. */
export function relativeTime(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "—";
  const seconds = (now - new Date(iso).getTime()) / 1000;
  if (!Number.isFinite(seconds)) return "—";
  for (const [unit, size] of UNITS) {
    if (Math.abs(seconds) >= size) return rtf.format(-Math.floor(seconds / size), unit);
  }
  return "just now";
}

/** The full local date and time, for tooltips next to a relative time. */
export const fullTime = (iso: string | null | undefined) =>
  iso && Number.isFinite(new Date(iso).getTime()) ? new Date(iso).toLocaleString() : undefined;
