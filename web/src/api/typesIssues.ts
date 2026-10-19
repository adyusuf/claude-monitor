// The API's shapes for issues and the backlog (ADR-0011 phase 2), as IssueModels.cs sends them. Type-only.
import type { ItemType } from "./typesProjects";

export type IssueStatus = "backlog" | "ready" | "in_progress" | "in_review" | "done" | "cancelled";
export type Severity = "critical" | "major" | "minor" | "trivial";
export type TestResult = "not_run" | "passed" | "failed" | "blocked";
export type CodeLinkKind = "pr" | "commit";
export type CodeLinkSource = "manual" | "auto";
/** "blocks": this issue blocks the other; "blocked_by": the other blocks this one. */
export type LinkKind = "blocks" | "blocked_by";

export interface CriterionEntry { given: string | null; when: string | null; then: string | null }

/**
 * A PUT replaces the issue. Optional texts and ids that are missing are cleared; a missing type and status keep their value.
 * For priority and estimate a missing value keeps the current one and an empty string clears it.
 */
export interface IssueInput {
  title: string;
  type?: ItemType;
  priority?: string;
  estimate?: string;
  status?: IssueStatus;
  assigneeUserId?: string | null;
  parentId?: string | null;
  applicationId?: string | null;
  componentId?: string | null;
  releaseId?: string | null;
  description?: string | null;
  acceptanceCriteria?: CriterionEntry[];
  technicalDesign?: string | null;
  bugEnvironment?: string | null;
  reproSteps?: string | null;
  expectedResult?: string | null;
  actualResult?: string | null;
  severity?: Severity | null;
}

export interface IssueSummary {
  id: string;
  projectId: string;
  applicationId: string | null;
  number: number;
  title: string;
  type: ItemType;
  priority: string | null;
  estimate: string | null;
  status: IssueStatus;
  assigneeUserId: string | null;
  assigneeName: string | null;
  parentId: string | null;
  componentId: string | null;
  releaseId: string | null;
  position: number;
  archived: boolean;
  createdAt: string;
  updatedAt: string;
  /** The sprint the issue is in, if any. */
  sprintId: string | null;
}

export interface IssueLinkView { linkId: string; issueId: string; number: number; title: string; status: IssueStatus }

export interface IssueView {
  issue: IssueSummary;
  description: string | null;
  acceptanceCriteria: CriterionEntry[];
  technicalDesign: string | null;
  bugEnvironment: string | null;
  reproSteps: string | null;
  expectedResult: string | null;
  actualResult: string | null;
  severity: Severity | null;
  parentTitle: string | null;
  blocks: IssueLinkView[];
  blockedBy: IssueLinkView[];
  canEdit: boolean;
}

/** The filters of a project's issue list; every one is optional. */
export type IssueQuery = {
  q?: string;
  type?: string;
  status?: string;
  assignee?: string;
  application?: string;
  component?: string;
  parent?: string;
  release?: string;
  sprint?: string;
  scope?: "backlog";
  archived?: boolean;
  cursor?: string | null;
  limit?: number;
};

export interface TestCaseInput { title: string; steps: string | null; expected: string | null }

export interface TestCaseView {
  id: string;
  issueId: string;
  title: string;
  steps: string | null;
  expected: string | null;
  result: TestResult;
  resultNote: string | null;
  resultBy: string | null;
  resultByName: string | null;
  resultAt: string | null;
  createdAt: string;
}

export interface CodeLinkInput { kind: CodeLinkKind; url: string; title: string | null }

export interface CodeLinkView {
  id: string;
  issueId: string;
  kind: CodeLinkKind;
  url: string;
  title: string | null;
  source: CodeLinkSource;
  createdAt: string;
}
