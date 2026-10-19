// The fixed vocabularies and limits of issues (ADR-0011 phase 2); the API checks the same lists.
import type { EstimationUnit, PriorityScale } from "../api/typesProjects";
import type { CodeLinkKind, IssueStatus, LinkKind, Severity, TestResult } from "../api/typesIssues";

export const ISSUE_STATUSES: IssueStatus[] = ["backlog", "ready", "in_progress", "in_review", "done", "cancelled"];
export const SEVERITIES: Severity[] = ["critical", "major", "minor", "trivial"];
export const TEST_RESULTS: TestResult[] = ["not_run", "passed", "failed", "blocked"];
export const CODE_LINK_KINDS: CodeLinkKind[] = ["pr", "commit"];
export const LINK_KINDS: LinkKind[] = ["blocks", "blocked_by"];
export const TSHIRT_SIZES = ["xs", "s", "m", "l", "xl"] as const;

/** The values of each priority scale, highest first. */
export const PRIORITY_VALUES: Record<PriorityScale, string[]> = {
  p0_p3: ["p0", "p1", "p2", "p3"],
  moscow: ["must", "should", "could", "wont"],
  high_medium_low: ["high", "medium", "low"],
  one_to_five: ["1", "2", "3", "4", "5"],
};

/** The status filter and the quick form choose between these; the backlog scope shows only the first two. */
export const BACKLOG_STATUSES: IssueStatus[] = ["backlog", "ready"];

export const issueLimits = {
  titleMax: 200,
  textMax: 20_000,
  lineMax: 500,
  stepsMax: 5000,
  resultMax: 2000,
  environmentMax: 200,
  criteriaMax: 30,
  estimateMax: 9999.99,
  linkTitleMax: 200,
  urlMax: 500,
  noteMax: 2000,
  searchLimit: 20,
  epicLimit: 100,
  /** The recent workspace runs an issue's picker reads, and the projects the run list's pickers read. */
  runsLimit: 50,
  projectsLimit: 50,
} as const;

/** True for an estimate the unit allows: a number of up to two decimals (0 to 9999.99), or a T-shirt size. */
export function estimateOk(value: string, unit: EstimationUnit): boolean {
  if (unit === "t_shirt") return (TSHIRT_SIZES as readonly string[]).includes(value);
  return /^\d{1,4}(\.\d{1,2})?$/.test(value) && Number(value) <= issueLimits.estimateMax;
}
