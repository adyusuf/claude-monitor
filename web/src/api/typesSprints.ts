// The API's shapes for sprints (ADR-0011 phase 3), as SprintModels.cs sends them. Type-only.
// Dates are ISO "yyyy-mm-dd" (no time); the pages show them as dd/mm/yyyy.
import type { IssueStatus } from "./typesIssues";

export type SprintStatus = "planned" | "active" | "closed";

/** A PUT replaces the sprint: a missing goal, name, date, capacity, release or team is cleared. The application is read on create only. */
export interface SprintInput {
  name: string | null;
  goal: string | null;
  startDate: string | null;
  endDate: string | null;
  capacity: number | null;
  applicationId?: string | null;
  releaseId: string | null;
  teamUserIds: string[];
}

export interface SprintSummary {
  id: string;
  projectId: string;
  applicationId: string | null;
  number: number;
  name: string | null;
  goal: string | null;
  startDate: string | null;
  endDate: string | null;
  capacity: number | null;
  status: SprintStatus;
  releaseId: string | null;
  releaseName: string | null;
  issueCount: number;
  /** The sums of the numeric estimates of the sprint's issues; always 0 in a T-shirt project. */
  committed: number;
  done: number;
  startedAt: string | null;
  closedAt: string | null;
  createdAt: string;
}

export interface SprintMemberView { userId: string; displayName: string }

export interface SprintView {
  sprint: SprintSummary;
  team: SprintMemberView[];
  /** How many issues the sprint has in each status (a status with none may be missing). */
  statusCounts: Partial<Record<IssueStatus, number>>;
  canEdit: boolean;
}

/** The filters of a project's sprint list; every one is optional. */
export type SprintQuery = { status?: SprintStatus; application?: string; cursor?: string | null; limit?: number };
