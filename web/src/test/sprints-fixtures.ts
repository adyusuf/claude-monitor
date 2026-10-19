import type { IssueSummary } from "../api/typesIssues";
import type { SprintSummary, SprintView } from "../api/typesSprints";
import { issueSummary } from "./issues-fixtures";

/** A planned project-wide sprint with dates, a capacity of 20 (hours in the test project) and 12 committed. */
export const sprintSummary = (over: Partial<SprintSummary> = {}): SprintSummary => ({
  id: "s1", projectId: "p1", applicationId: null, number: 1, name: "Launch", goal: "Ship the checkout", startDate: "2026-03-02", endDate: "2026-03-13",
  capacity: 20, status: "planned", releaseId: null, releaseName: null, issueCount: 3, committed: 12, done: 4, startedAt: null, closedAt: null,
  createdAt: "2026-02-20T10:00:00Z", ...over,
});

export const sprintView = (over: Partial<SprintView> = {}, sprint: Partial<SprintSummary> = {}): SprintView => ({
  sprint: sprintSummary(sprint), team: [{ userId: "u2", displayName: "Deniz" }], statusCounts: { backlog: 1, in_progress: 1, done: 1 }, canEdit: true, ...over,
});

/** One sprint of each status, newest number first (the order the API lists them). */
export const threeSprints = (): SprintSummary[] => [
  sprintSummary({ id: "s3", number: 3, name: "Polish", status: "planned", startDate: null, endDate: null, capacity: null, issueCount: 0, committed: 0, done: 0 }),
  sprintSummary({ id: "s2", number: 2, name: "Payments", status: "active", releaseId: "r1", releaseName: "1.0.0", startedAt: "2026-03-02T08:00:00Z" }),
  sprintSummary({ id: "s1", number: 1, name: null, goal: null, status: "closed", startDate: "2026-02-16", endDate: "2026-02-27", closedAt: "2026-02-27T17:00:00Z" }),
];

/** The issues of sprint s1 in three statuses: #1 in progress, #2 ready, #3 done. */
export const sprintIssues = (): IssueSummary[] => [
  issueSummary({ id: "i1", number: 1, title: "Pay with a card", status: "in_progress", sprintId: "s1" }),
  issueSummary({ id: "i2", number: 2, title: "Refund an order", type: "bug", estimate: "2.5", status: "ready", assigneeUserId: null, assigneeName: null, sprintId: "s1" }),
  issueSummary({ id: "i3", number: 3, title: "Print a receipt", status: "done", priority: null, estimate: null, sprintId: "s1" }),
];

/** Issues of the backlog that can go into a sprint. */
export const candidates = (): IssueSummary[] => [
  issueSummary({ id: "b1", number: 11, title: "Gift cards", status: "backlog" }),
  issueSummary({ id: "b2", number: 12, title: "Wish list", status: "ready", type: "task", estimate: null }),
];
