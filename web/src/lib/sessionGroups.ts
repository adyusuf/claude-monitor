import type { SessionRow } from "../api/types";

/** The sessions of one branch on one machine, newest activity first. */
export interface BranchGroup { branch: string | null; rows: SessionRow[]; latest: number }
/** The branches of one machine, the one with the newest activity first. */
export interface MachineGroup { hostname: string; branches: BranchGroup[]; latest: number; count: number }
/** What a machine's card showed: its branches and, per branch, its sessions, top to bottom. */
export interface MachineOrder { branches: (string | null)[]; rows: Record<string, string[]> }

const at = (s: SessionRow): number => {
  const ms = Date.parse(s.lastEventAt);
  return Number.isFinite(ms) ? ms : 0;
};

/** A branch the harness did not report is one group, shown under its own label; an empty name is the same as none. */
const branchOf = (s: SessionRow): string | null => s.gitBranch?.trim() || null;
const branchKey = (branch: string | null): string => branch ?? "";

/** The order a machine's card shows now, to keep while the reader points at it. */
export const orderOf = (group: MachineGroup): MachineOrder => ({
  branches: group.branches.map((b) => b.branch),
  rows: Object.fromEntries(group.branches.map((b) => [branchKey(b.branch), b.rows.map((s) => s.id)])),
});

/** Sorts by position in `kept`, newest first; whatever `kept` does not know goes after what it does, newest first. */
function keepOrder<T>(items: T[], id: (item: T) => string, latest: (item: T) => number, kept: readonly string[] | undefined): T[] {
  const rank = new Map((kept ?? []).map((k, i) => [k, i]));
  const last = Number.MAX_SAFE_INTEGER;
  return [...items].sort((a, b) => (rank.get(id(a)) ?? last) - (rank.get(id(b)) ?? last) || latest(b) - latest(a));
}

/**
 * Sessions grouped by machine, then by branch. The machines keep a fixed order (by name), so a card never moves under the reader;
 * inside a machine the branch and the sessions with the newest activity come first. A machine in `frozen` keeps the order it was
 * shown in (new arrivals go after it) while the data in it stays current.
 */
export function groupSessions(rows: SessionRow[], frozen: Readonly<Record<string, MachineOrder>> = {}): MachineGroup[] {
  const machines = new Map<string, Map<string | null, SessionRow[]>>();
  for (const s of rows) {
    const branches = machines.get(s.hostname) ?? new Map<string | null, SessionRow[]>();
    const list = branches.get(branchOf(s)) ?? [];
    list.push(s);
    branches.set(branchOf(s), list);
    machines.set(s.hostname, branches);
  }
  const groups = [...machines].map(([hostname, branches]) => {
    const keep = frozen[hostname];
    const made: BranchGroup[] = [...branches].map(([branch, members]) => {
      const sorted = keepOrder(members, (s) => s.id, at, keep?.rows[branchKey(branch)]);
      return { branch, rows: sorted, latest: Math.max(...members.map(at)) };
    });
    const list = keepOrder(made, (g) => branchKey(g.branch), (g) => g.latest, keep?.branches.map(branchKey));
    return { hostname, branches: list, latest: Math.max(...list.map((g) => g.latest)), count: list.reduce((n, g) => n + g.rows.length, 0) };
  });
  return groups.sort((a, b) => a.hostname.localeCompare(b.hostname, undefined, { sensitivity: "base" }) || (a.hostname < b.hostname ? -1 : a.hostname > b.hostname ? 1 : 0));
}
