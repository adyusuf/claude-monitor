import type { SessionRow } from "../api/types";

/** A machine the list shows even when it has no session: its agent and its name. */
export interface MachineSeed { agentId: string; hostname: string }
/** The sessions of one repository on one machine, newest activity first. */
export interface RepoGroup { repo: string | null; rows: SessionRow[]; latest: number }
/** One machine: its repositories, the one with the newest activity first. `key` is the agent (or, for a machine not in the seeds, its name). */
export interface MachineGroup { key: string; agentId: string | null; hostname: string; repos: RepoGroup[]; latest: number; count: number }
/** What a machine's card showed: its repositories and, per repository, its sessions, top to bottom. */
export interface MachineOrder { repos: (string | null)[]; rows: Record<string, string[]> }

const at = (s: SessionRow): number => {
  const ms = Date.parse(s.lastEventAt);
  return Number.isFinite(ms) ? ms : 0;
};

/** A session with no repository is one group, shown under its own label; an empty name is the same as none. */
const repoOf = (s: SessionRow): string | null => s.projectName?.trim() || null;
const repoKey = (repo: string | null): string => repo ?? "";

/** The order a machine's card shows now, to keep while the reader points at it. */
export const orderOf = (group: MachineGroup): MachineOrder => ({
  repos: group.repos.map((r) => r.repo),
  rows: Object.fromEntries(group.repos.map((r) => [repoKey(r.repo), r.rows.map((s) => s.id)])),
});

/** Sorts by position in `kept`, newest first; whatever `kept` does not know goes after what it does, newest first. */
function keepOrder<T>(items: T[], id: (item: T) => string, latest: (item: T) => number, kept: readonly string[] | undefined): T[] {
  const rank = new Map((kept ?? []).map((k, i) => [k, i]));
  const last = Number.MAX_SAFE_INTEGER;
  return [...items].sort((a, b) => (rank.get(id(a)) ?? last) - (rank.get(id(b)) ?? last) || latest(b) - latest(a));
}

const byName = (a: { hostname: string }, b: { hostname: string }) =>
  a.hostname.localeCompare(b.hostname, undefined, { sensitivity: "base" }) || (a.hostname < b.hostname ? -1 : a.hostname > b.hostname ? 1 : 0);

/**
 * Every machine first, then its sessions grouped by repository. The machines come from `machines` (so one with no session is still listed) and
 * from the sessions (a machine no longer connected that still has sessions); they keep a fixed order by name, so a card never moves under the
 * reader. Inside a machine the repository and the sessions with the newest activity come first. A machine in `frozen` keeps the order it was
 * shown in (new arrivals go after it) while the data in it stays current.
 */
export function groupSessions(rows: SessionRow[], machines: readonly MachineSeed[] = [], frozen: Readonly<Record<string, MachineOrder>> = {}): MachineGroup[] {
  const seeds = new Map(machines.map((m) => [m.agentId, m]));
  const found = new Map<string, { agentId: string | null; hostname: string; repos: Map<string | null, SessionRow[]> }>();
  for (const m of machines) found.set(m.agentId, { agentId: m.agentId, hostname: m.hostname, repos: new Map() });
  for (const s of rows) {
    const key = seeds.has(s.agentId) ? s.agentId : `host:${s.hostname}`;
    const machine = found.get(key) ?? { agentId: null, hostname: s.hostname, repos: new Map<string | null, SessionRow[]>() };
    const list = machine.repos.get(repoOf(s)) ?? [];
    list.push(s);
    machine.repos.set(repoOf(s), list);
    found.set(key, machine);
  }
  const groups = [...found].map(([key, machine]) => {
    const keep = frozen[key];
    const made: RepoGroup[] = [...machine.repos].map(([repo, members]) => ({
      repo, rows: keepOrder(members, (s) => s.id, at, keep?.rows[repoKey(repo)]), latest: Math.max(...members.map(at)),
    }));
    const repos = keepOrder(made, (g) => repoKey(g.repo), (g) => g.latest, keep?.repos.map(repoKey));
    return {
      key, agentId: machine.agentId, hostname: machine.hostname, repos,
      latest: repos.length === 0 ? 0 : Math.max(...repos.map((g) => g.latest)), count: repos.reduce((n, g) => n + g.rows.length, 0),
    };
  });
  return groups.sort(byName);
}
