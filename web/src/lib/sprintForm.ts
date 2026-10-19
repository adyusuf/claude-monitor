// The editable form of a sprint and the checks the form runs before it asks the API (the API checks again, global #7).
// A form holds text; the input sent to the API holds numbers, nulls and trimmed texts.
import type { Member } from "../api/types";
import type { ProjectView } from "../api/typesProjects";
import type { SprintInput, SprintView } from "../api/typesSprints";
import type { FieldErrors } from "./projectForm";
import { addDays, sprintLimits as limit, today } from "./sprintOptions";

export interface SprintDraft {
  name: string;
  goal: string;
  startDate: string;
  endDate: string;
  capacity: string;
  releaseId: string;
  teamUserIds: string[];
}

/** A new sprint starts today and ends after the project's default sprint length. */
export const emptySprintDraft = (project: ProjectView, from: string = today()): SprintDraft => ({
  name: "", goal: "", startDate: from, endDate: addDays(from, project.sprintLengthDays), capacity: "", releaseId: "", teamUserIds: [],
});

export const draftOfSprint = (view: SprintView): SprintDraft => ({
  name: view.sprint.name ?? "", goal: view.sprint.goal ?? "", startDate: view.sprint.startDate ?? "", endDate: view.sprint.endDate ?? "",
  capacity: view.sprint.capacity === null ? "" : String(view.sprint.capacity), releaseId: view.sprint.releaseId ?? "",
  teamUserIds: view.team.map((m) => m.userId),
});

const text = (value: string): string | null => value.trim() === "" ? null : value.trim();

export const inputOfSprint = (d: SprintDraft, applicationId?: string | null): SprintInput => ({
  name: text(d.name), goal: text(d.goal), startDate: text(d.startDate), endDate: text(d.endDate),
  capacity: d.capacity.trim() === "" ? null : Number(d.capacity), ...(applicationId === undefined ? {} : { applicationId }),
  releaseId: d.releaseId || null, teamUserIds: d.teamUserIds,
});

const capacityOk = (value: string): boolean => {
  if (value.trim() === "") return true;
  const n = Number(value);
  return Number.isFinite(n) && n >= 0 && n <= limit.capacityMax && Math.round(n * 100) / 100 === n;
};

/** The first problem of each field, by the codes the API would answer with; empty when the form can be sent. */
export function checkSprint(d: SprintDraft): FieldErrors {
  const errors: FieldErrors = {};
  if (d.name.trim().length > limit.nameMax) errors.name = "too_long";
  if (d.goal.trim().length > limit.goalMax) errors.goal = "too_long";
  if (d.startDate && d.endDate && d.endDate < d.startDate) errors.endDate = "before_start";
  if (!capacityOk(d.capacity)) errors.capacity = "out_of_range";
  if (d.teamUserIds.length > limit.teamMax) errors.teamUserIds = "invalid_list";
  return errors;
}

/** The people who can be in a team: workspace members who are not viewers, and whoever is in it already (whatever their role is now). */
export const teamCandidates = (members: Member[], teamUserIds: string[]): Member[] =>
  members.filter((m) => m.role !== "viewer" || teamUserIds.includes(m.userId));
