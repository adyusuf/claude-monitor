// The editable form of an issue and the checks the form runs before it asks the API (the API checks again, global #7).
// A form holds text; the input sent to the API holds trimmed text and nulls.
import type { CriterionEntry, IssueInput, IssueStatus, IssueView, Severity } from "../api/typesIssues";
import type { ItemType, ProjectView } from "../api/typesProjects";
import { estimateOk, issueLimits as limit, PRIORITY_VALUES } from "./issueOptions";
import type { FieldErrors } from "./projectForm";

export interface CriterionDraft { given: string; when: string; then: string }

export interface IssueDraft {
  title: string;
  type: ItemType;
  priority: string;
  estimate: string;
  status: IssueStatus;
  assigneeUserId: string;
  parentId: string;
  applicationId: string;
  componentId: string;
  releaseId: string;
  description: string;
  acceptanceCriteria: CriterionDraft[];
  technicalDesign: string;
  bugEnvironment: string;
  reproSteps: string;
  expectedResult: string;
  actualResult: string;
  severity: Severity | "";
}

export const draftOfIssue = (v: IssueView): IssueDraft => ({
  title: v.issue.title, type: v.issue.type, priority: v.issue.priority ?? "", estimate: v.issue.estimate ?? "", status: v.issue.status,
  assigneeUserId: v.issue.assigneeUserId ?? "", parentId: v.issue.parentId ?? "", applicationId: v.issue.applicationId ?? "",
  componentId: v.issue.componentId ?? "", releaseId: v.issue.releaseId ?? "", description: v.description ?? "",
  acceptanceCriteria: v.acceptanceCriteria.map((c) => ({ given: c.given ?? "", when: c.when ?? "", then: c.then ?? "" })),
  technicalDesign: v.technicalDesign ?? "", bugEnvironment: v.bugEnvironment ?? "", reproSteps: v.reproSteps ?? "",
  expectedResult: v.expectedResult ?? "", actualResult: v.actualResult ?? "", severity: v.severity ?? "",
});

const text = (value: string): string | null => value.trim() === "" ? null : value.trim();

/** Criteria rows with at least one part filled in; the API refuses an empty row. */
const criteriaOf = (rows: CriterionDraft[]): CriterionEntry[] =>
  rows.filter((r) => r.given.trim() || r.when.trim() || r.then.trim()).map((r) => ({ given: text(r.given), when: text(r.when), then: text(r.then) }));

/** Only a bug carries the bug fields, and an epic has no parent epic; what does not apply is sent as null so the API clears it. */
export function inputOfIssue(d: IssueDraft): IssueInput {
  const bug = d.type === "bug";
  return {
    title: d.title.trim(), type: d.type, priority: d.priority, estimate: d.estimate.trim(), status: d.status,
    assigneeUserId: d.assigneeUserId || null, parentId: d.type === "epic" ? null : d.parentId || null, applicationId: d.applicationId || null,
    componentId: d.applicationId ? d.componentId || null : null, releaseId: d.applicationId ? d.releaseId || null : null,
    description: text(d.description), acceptanceCriteria: criteriaOf(d.acceptanceCriteria), technicalDesign: text(d.technicalDesign),
    bugEnvironment: bug ? text(d.bugEnvironment) : null, reproSteps: bug ? text(d.reproSteps) : null,
    expectedResult: bug ? text(d.expectedResult) : null, actualResult: bug ? text(d.actualResult) : null, severity: bug && d.severity ? d.severity : null,
  };
}

const tooLong = (value: string, max: number) => value.trim().length > max;

/** The first problem of each field, by the codes the API would answer with; empty when the form can be sent. */
export function checkIssue(d: IssueDraft, project: ProjectView): FieldErrors {
  const errors: FieldErrors = {};
  if (d.title.trim() === "" || tooLong(d.title, limit.titleMax)) errors.title = "invalid_name";
  if (d.priority !== "" && !PRIORITY_VALUES[project.priorityScale].includes(d.priority)) errors.priority = "invalid_value";
  if (d.estimate.trim() !== "" && !estimateOk(d.estimate.trim(), project.estimationUnit)) errors.estimate = "invalid_value";
  if (tooLong(d.description, limit.textMax)) errors.description = "too_long";
  if (tooLong(d.technicalDesign, limit.textMax)) errors.technicalDesign = "too_long";
  const rows = d.acceptanceCriteria;
  if (rows.length > limit.criteriaMax || rows.some((r) => [r.given, r.when, r.then].some((part) => tooLong(part, limit.lineMax)))) {
    errors.acceptanceCriteria = "invalid_list";
  }
  if (d.type === "bug") {
    for (const [field, value, max] of [["bugEnvironment", d.bugEnvironment, limit.environmentMax], ["reproSteps", d.reproSteps, limit.stepsMax],
      ["expectedResult", d.expectedResult, limit.resultMax], ["actualResult", d.actualResult, limit.resultMax]] as const) {
      if (tooLong(value, max)) errors[field] = "too_long";
    }
  }
  return errors;
}

/** A pull request or commit address: http or https, no user information, within the length the API takes. */
export function codeLinkUrlOk(value: string): boolean {
  if (value.length > limit.urlMax) return false;
  try {
    const url = new URL(value);
    return ["https:", "http:"].includes(url.protocol) && url.hostname !== "" && url.username === "" && url.password === "";
  } catch {
    return false;
  }
}
