// Dates, money and counts as the pages show them. Dates are always dd/mm/yyyy (global #12): built from the parts,
// never through a locale-aware formatter. The API sends UTC ISO-8601; the page shows the viewer's local time.

const pad = (n: number) => String(n).padStart(2, "0");

export function date(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  return `${date(iso)} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function time(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** "now", "5m", "3h", "2d": a short age; beyond a week, the date. Translated units come from the caller. */
export function age(iso: string, now: number, units: { now: string; m: string; h: string; d: string }): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (seconds < 45) return units.now;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}${units.m}`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}${units.h}`;
  const days = Math.round(hours / 24);
  return days < 8 ? `${days}${units.d}` : date(iso);
}

/** "24m", "1h 05m": the time left until iso; null when it is not a future moment (or not a date at all). */
export function remaining(iso: string | null | undefined, now: number, units: { m: string; h: string }): string | null {
  const left = iso ? new Date(iso).getTime() - now : NaN;
  if (!(left > 0)) return null;
  const minutes = Math.max(1, Math.ceil(left / 60_000));
  return minutes < 60 ? `${minutes}${units.m}` : `${Math.floor(minutes / 60)}${units.h} ${pad(minutes % 60)}${units.m}`;
}

/** "$1.23"; tiny amounts keep 4 decimals; null is "cannot be measured" (the caller's text). */
export function usd(value: number | null | undefined, unmeasured: string): string {
  if (value === null || value === undefined) return unmeasured;
  return value !== 0 && Math.abs(value) < 0.01 ? `$${value.toFixed(4)}` : `$${value.toFixed(2)}`;
}

/** 1234 -> "1.2k", 3_400_000 -> "3.4M". */
export function count(n: number): string {
  if (n < 1000) return String(n);
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`;
  return `${(n / 1_000_000).toFixed(1)}M`;
}

export function bytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}
