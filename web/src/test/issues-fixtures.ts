import type { CodeLinkView, IssueLinkView, IssueRunView, IssueSummary, IssueView, RunIssueView, TestCaseView } from "../api/typesIssues";

export const issueSummary = (over: Partial<IssueSummary> = {}): IssueSummary => ({
  id: "i1", projectId: "p1", applicationId: null, number: 1, title: "Pay with a card", type: "story", priority: "must", estimate: "5", status: "backlog",
  assigneeUserId: "u2", assigneeName: "Deniz", parentId: null, componentId: null, releaseId: null, position: 1, archived: false,
  createdAt: "2026-02-01T10:00:00Z", updatedAt: "2026-02-02T10:00:00Z", sprintId: null, ...over,
});

/** Three issues in backlog order: #1, #2, #3. */
export const threeIssues = (): IssueSummary[] => [
  issueSummary(),
  issueSummary({ id: "i2", number: 2, title: "Refund an order", type: "bug", priority: "should", estimate: "2.5", status: "ready", assigneeUserId: null, assigneeName: null, position: 2, parentId: "e1" }),
  issueSummary({ id: "i3", number: 3, title: "Print a receipt", type: "story", priority: null, estimate: null, position: 3 }),
];

export const link = (over: Partial<IssueLinkView> = {}): IssueLinkView => ({
  linkId: "l1", issueId: "i2", number: 2, title: "Refund an order", status: "ready", ...over,
});

export const issueView = (over: Partial<IssueView> = {}, issue: Partial<IssueSummary> = {}): IssueView => ({
  issue: issueSummary(issue), description: "As a buyer I want to pay so that I own it", acceptanceCriteria: [{ given: "a full cart", when: "I pay", then: "I get a receipt" }],
  technicalDesign: "Use the provider SDK", bugEnvironment: null, reproSteps: null, expectedResult: null, actualResult: null, severity: null, parentTitle: null,
  blocks: [], blockedBy: [], canEdit: true, ...over,
});

export const bugView = (): IssueView => issueView({
  bugEnvironment: "Safari 19", reproSteps: "Click refund twice", expectedResult: "One refund", actualResult: "Two refunds", severity: "major",
}, { id: "i2", number: 2, title: "Refund an order", type: "bug", parentId: "e1", priority: "should", estimate: "2.5" });

export const testCase = (over: Partial<TestCaseView> = {}): TestCaseView => ({
  id: "t1", issueId: "i1", title: "Pays by card", steps: "Open the cart", expected: "Order is paid", result: "not_run", resultNote: null,
  resultBy: null, resultByName: null, resultAt: null, createdAt: "2026-02-03T10:00:00Z", ...over,
});

export const codeLink = (over: Partial<CodeLinkView> = {}): CodeLinkView => ({
  id: "c1", issueId: "i1", kind: "pr", url: "https://git.example.org/shop/pull/7", title: "Card payments", source: "manual", createdAt: "2026-02-04T10:00:00Z", ...over,
});

export const issueRun = (over: Partial<IssueRunView> = {}): IssueRunView => ({
  runId: "r1", kind: "test", status: "failed", exitCode: 1, testsPassed: 40, testsFailed: 2, testsSkipped: 1, targetHostname: "build-box", projectKey: "shop",
  createdAt: "2026-03-05T09:00:00Z", endedAt: "2026-03-05T09:05:00Z", linkedAt: "2026-03-05T10:00:00Z", ...over,
});

export const runIssue = (over: Partial<RunIssueView> = {}): RunIssueView => ({ issueId: "i1", projectId: "p1", number: 1, title: "Pay with a card", status: "backlog", ...over });
