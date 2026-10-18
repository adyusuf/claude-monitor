import type { SessionRow } from "../api/types";

/** The sessions of one branch on one machine, newest activity first. */
export interface BranchGroup { branch: string | null; rows: SessionRow[]; latest: number }
/** The branches of one machine, the one with the newest activity first. */
export interface MachineGroup { hostname: string; branches: BranchGroup[]; latest: number; count: number }

const at = (s: SessionRow): number => {
  const ms = Date.parse(s.lastEventAt);
  return Number.isFinite(ms) ? ms : 0;
};

/** A branch the harness did not report is one group, shown under its own label; an empty name is the same as none. */
const branchOf = (s: SessionRow): string | null => s.gitBranch?.trim() || null;

/**
 * Sessions grouped by machine, then by branch. Every level is ordered by its newest activity, the most recent first, so
 * the session someone just worked in is at the top of the page whatever the order the rows came in.
 */
export function groupSessions(rows: SessionRow[]): MachineGroup[] {
  const machines = new Map<string, Map<string | null, SessionRow[]>>();
  for (const s of rows) {
    const branches = machines.get(s.hostname) ?? new Map<string | null, SessionRow[]>();
    const list = branches.get(branchOf(s)) ?? [];
    list.push(s);
    branches.set(branchOf(s), list);
    machines.set(s.hostname, branches);
  }
  const groups = [...machines].map(([hostname, branches]) => {
    const list: BranchGroup[] = [...branches].map(([branch, members]) => {
      const sorted = [...members].sort((a, b) => at(b) - at(a));
      return { branch, rows: sorted, latest: at(sorted[0]!) };
    }).sort((a, b) => b.latest - a.latest);
    return { hostname, branches: list, latest: list[0]!.latest, count: list.reduce((n, g) => n + g.rows.length, 0) };
  });
  return groups.sort((a, b) => b.latest - a.latest);
}
