// The editable form of a project and the checks the form runs before it asks the API (the API checks again, global #7).
// A form holds text; the input sent to the API holds numbers, nulls and trimmed lines.
import type { ItemType, EstimationUnit, PriorityScale, ProjectInput, ProjectView, Stakeholder } from "../api/typesProjects";
import { ITEM_TYPES, projectLimits as limit } from "./projectOptions";

export interface ProjectDraft {
  name: string;
  purpose: string;
  ownerUserId: string;
  sponsor: string;
  stakeholders: { name: string; role: string }[];
  budgetAmount: string;
  budgetCurrency: string;
  startDate: string;
  endDate: string;
  itemTypes: ItemType[];
  priorityScale: PriorityScale;
  estimationUnit: EstimationUnit;
  sprintLengthDays: string;
  definitionOfReady: string[];
  definitionOfDone: string[];
}

/** A field name (as the API names it) mapped to an error code under "errors.". */
export type FieldErrors = Record<string, string>;

export const emptyProjectDraft = (ownerUserId: string): ProjectDraft => ({
  name: "", purpose: "", ownerUserId, sponsor: "", stakeholders: [], budgetAmount: "", budgetCurrency: "", startDate: "", endDate: "",
  itemTypes: [...ITEM_TYPES], priorityScale: "p0_p3", estimationUnit: "story_points", sprintLengthDays: "14",
  definitionOfReady: [], definitionOfDone: [],
});

export const draftOfProject = (p: ProjectView): ProjectDraft => ({
  name: p.name, purpose: p.purpose ?? "", ownerUserId: p.ownerUserId, sponsor: p.sponsor ?? "",
  stakeholders: p.stakeholders.map((s) => ({ name: s.name ?? "", role: s.role ?? "" })),
  budgetAmount: p.budgetAmount === null ? "" : String(p.budgetAmount), budgetCurrency: p.budgetCurrency ?? "",
  startDate: p.startDate ?? "", endDate: p.endDate ?? "", itemTypes: [...p.itemTypes], priorityScale: p.priorityScale,
  estimationUnit: p.estimationUnit, sprintLengthDays: String(p.sprintLengthDays),
  definitionOfReady: [...p.definitionOfReady], definitionOfDone: [...p.definitionOfDone],
});

const text = (value: string): string | null => value.trim() === "" ? null : value.trim();

/** The lines a person typed without the blank ones (the API refuses a blank line). */
export const filled = (lines: string[]): string[] => lines.map((l) => l.trim()).filter((l) => l !== "");

const stakeholdersOf = (d: ProjectDraft): Stakeholder[] =>
  d.stakeholders.filter((s) => s.name.trim() !== "" || s.role.trim() !== "").map((s) => ({ name: s.name.trim(), role: text(s.role) }));

export const inputOfProject = (d: ProjectDraft): ProjectInput => ({
  name: d.name.trim(), purpose: text(d.purpose), ownerUserId: d.ownerUserId || null, sponsor: text(d.sponsor),
  stakeholders: stakeholdersOf(d),
  budgetAmount: d.budgetAmount.trim() === "" ? null : Number(d.budgetAmount), budgetCurrency: text(d.budgetCurrency),
  startDate: text(d.startDate), endDate: text(d.endDate), itemTypes: d.itemTypes, priorityScale: d.priorityScale,
  estimationUnit: d.estimationUnit, sprintLengthDays: Number(d.sprintLengthDays),
  definitionOfReady: filled(d.definitionOfReady), definitionOfDone: filled(d.definitionOfDone),
});

const tooLongLine = (lines: string[]) => filled(lines).length > limit.listMax || filled(lines).some((l) => l.length > limit.lineMax);

/** The first problem of each field, by the codes the API would answer with; empty when the form can be sent. */
export function checkProject(d: ProjectDraft): FieldErrors {
  const errors: FieldErrors = {};
  const input = inputOfProject(d);
  if (input.name === "" || input.name.length > limit.nameMax) errors.name = "invalid_name";
  if ((input.purpose?.length ?? 0) > limit.purposeMax) errors.purpose = "too_long";
  if ((input.sponsor?.length ?? 0) > limit.sponsorMax) errors.sponsor = "too_long";
  if (input.stakeholders.length > limit.stakeholdersMax
    || input.stakeholders.some((s) => !s.name || s.name.length > limit.nameMax || (s.role?.length ?? 0) > limit.nameMax)) errors.stakeholders = "invalid_list";
  if ((input.budgetAmount === null) !== (input.budgetCurrency === null)) errors.budget = "amount_and_currency";
  else if (input.budgetAmount !== null && !(input.budgetAmount >= 0 && input.budgetAmount <= limit.budgetMax)) errors.budgetAmount = "out_of_range";
  else if (input.budgetCurrency !== null && !/^[A-Z]{3}$/.test(input.budgetCurrency)) errors.budgetCurrency = "invalid_currency";
  if (input.startDate && input.endDate && input.endDate < input.startDate) errors.endDate = "before_start";
  if (input.itemTypes.length === 0) errors.itemTypes = "invalid_list";
  if (!Number.isInteger(input.sprintLengthDays) || input.sprintLengthDays < limit.sprintMin || input.sprintLengthDays > limit.sprintMax) errors.sprintLengthDays = "out_of_range";
  if (tooLongLine(d.definitionOfReady)) errors.definitionOfReady = "invalid_list";
  if (tooLongLine(d.definitionOfDone)) errors.definitionOfDone = "invalid_list";
  return errors;
}
