// The fixed vocabularies and limits of sprints (ADR-0011 phase 3); the API checks the same lists and limits.
import type { IssueStatus } from "../api/typesIssues";
import type { EstimationUnit } from "../api/typesProjects";
import type { SprintStatus } from "../api/typesSprints";

export const SPRINT_STATUSES: SprintStatus[] = ["planned", "active", "closed"];

/** The statuses that still need work; closing a sprint moves the issues in these somewhere else. */
export const UNFINISHED_STATUSES: IssueStatus[] = ["backlog", "ready", "in_progress", "in_review"];

export const sprintLimits = {
  nameMax: 100,
  goalMax: 1000,
  teamMax: 50,
  capacityMax: 99_999_999,
  /** The most issues one "add" call takes. */
  addMax: 50,
} as const;

/** A numeric unit sums into the committed/done bars; T-shirt sizes cannot be summed. */
export const unitIsNumeric = (unit: EstimationUnit): boolean => unit !== "t_shirt";

const pad = (n: number) => String(n).padStart(2, "0");

/** Today's calendar day ("yyyy-mm-dd") on this computer's clock. */
export function today(now: Date = new Date()): string {
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** The calendar day n days after a day ("yyyy-mm-dd"); an unreadable day is returned as it is. */
export function addDays(isoDay: string, n: number): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDay);
  if (!match) return isoDay;
  const moved = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + n));
  return `${moved.getUTCFullYear()}-${pad(moved.getUTCMonth() + 1)}-${pad(moved.getUTCDate())}`;
}

/** The unfinished issues of a sprint, from its per-status counts. */
export const unfinishedCount = (counts: Partial<Record<IssueStatus, number>>): number =>
  UNFINISHED_STATUSES.reduce((sum, s) => sum + (counts[s] ?? 0), 0);
