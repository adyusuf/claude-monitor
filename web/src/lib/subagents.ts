import type { SubagentRow } from "../api/types";

const ms = (iso: string | null | undefined): number => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(t) ? t : 0;
};

/** A subagent with no end yet is still running. */
export const isRunning = (r: SubagentRow): boolean => !r.endedAt;

/**
 * Every subagent the session ran, none dropped: the ones still running first, the most recently started on top; then the finished ones,
 * the most recently finished on top.
 */
export function orderSubagents(rows: SubagentRow[]): SubagentRow[] {
  const running = rows.filter(isRunning).sort((a, b) => ms(b.startedAt) - ms(a.startedAt));
  const finished = rows.filter((r) => !isRunning(r)).sort((a, b) => ms(b.endedAt) - ms(a.endedAt));
  return [...running, ...finished];
}

/** How long a subagent ran (until `now` while it still runs); null when its times cannot be read. */
export function runtimeMs(r: SubagentRow, now: number): number | null {
  const start = Date.parse(r.startedAt);
  const end = r.endedAt ? Date.parse(r.endedAt) : now;
  return Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : null;
}
