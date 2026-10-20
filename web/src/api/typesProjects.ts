// The API's shapes for managed projects and applications (ADR-0011), as ProjectModels.cs sends them. Type-only.
// Dates are ISO "yyyy-mm-dd" (no time); the pages show them as dd/mm/yyyy.

export type ProjectStatus = "active" | "archived";
export type ProjectRole = "manager" | "contributor" | "reviewer" | "viewer";
export type ItemType = "epic" | "story" | "task" | "bug" | "spike";
export type PriorityScale = "p0_p3" | "moscow" | "high_medium_low" | "one_to_five";
export type EstimationUnit = "story_points" | "hours" | "days" | "t_shirt";
export type ApplicationKind = "web" | "mobile" | "api" | "other";
export type EnvironmentName = "dev" | "test" | "prod";
export type VersioningScheme = "semver" | "calver" | "none";
export type TargetKind = "performance" | "accessibility" | "sla" | "security" | "other";
export type ComponentKind = "component" | "module";
export type ReleaseStatus = "planned" | "released" | "cancelled";

export interface Stakeholder { name: string | null; role: string | null }

export interface ProjectInput {
  name: string;
  purpose: string | null;
  ownerUserId: string | null;
  sponsor: string | null;
  stakeholders: Stakeholder[];
  budgetAmount: number | null;
  budgetCurrency: string | null;
  startDate: string | null;
  endDate: string | null;
  itemTypes: ItemType[];
  priorityScale: PriorityScale;
  estimationUnit: EstimationUnit;
  sprintLengthDays: number;
  definitionOfReady: string[];
  definitionOfDone: string[];
}

export interface ProjectAccess { canEdit: boolean; canArchive: boolean; canEditApplications: boolean }

export interface ProjectSummary {
  id: string;
  name: string;
  purpose: string | null;
  ownerUserId: string;
  ownerName: string | null;
  startDate: string | null;
  endDate: string | null;
  status: ProjectStatus;
  applicationCount: number;
  createdAt: string;
  /** The work tracker connection the project mirrors (ADR-0015); null for a project made in Podium. */
  sourceConnectionId: string | null;
}

export interface ProjectView {
  id: string;
  workspaceId: string;
  name: string;
  purpose: string | null;
  ownerUserId: string;
  ownerName: string | null;
  sponsor: string | null;
  stakeholders: Stakeholder[];
  budgetAmount: number | null;
  budgetCurrency: string | null;
  startDate: string | null;
  endDate: string | null;
  itemTypes: ItemType[];
  priorityScale: PriorityScale;
  estimationUnit: EstimationUnit;
  sprintLengthDays: number;
  definitionOfReady: string[];
  definitionOfDone: string[];
  status: ProjectStatus;
  createdAt: string;
  updatedAt: string;
  access: ProjectAccess;
  sourceConnectionId: string | null;
}

export interface ProjectMemberView { userId: string; displayName: string; email: string | null; role: ProjectRole; addedAt: string }

export interface EnvironmentEntry { name: EnvironmentName | null; url: string | null }
export interface TargetEntry { kind: TargetKind | null; target: string | null }

export interface ApplicationInput {
  name: string;
  description: string | null;
  kind: ApplicationKind;
  repoUrl: string | null;
  environments: EnvironmentEntry[];
  techStack: string[];
  versioningScheme: VersioningScheme;
  techOwnerUserId: string | null;
  nonFunctionalTargets: TargetEntry[];
}

export interface ApplicationSummary {
  id: string;
  projectId: string;
  name: string;
  description: string | null;
  kind: ApplicationKind;
  techStack: string[];
  status: ProjectStatus;
  createdAt: string;
}

export interface ApplicationView {
  id: string;
  projectId: string;
  workspaceId: string;
  name: string;
  description: string | null;
  kind: ApplicationKind;
  repoUrl: string | null;
  environments: EnvironmentEntry[];
  techStack: string[];
  versioningScheme: VersioningScheme;
  techOwnerUserId: string | null;
  techOwnerName: string | null;
  nonFunctionalTargets: TargetEntry[];
  status: ProjectStatus;
  createdAt: string;
  updatedAt: string;
  canEdit: boolean;
}

export interface ComponentInput { name: string; kind: ComponentKind }
export interface ComponentView { id: string; applicationId: string; name: string; kind: ComponentKind; status: ProjectStatus; createdAt: string }

export interface ReleaseInput { name: string; plannedDate: string | null; status: ReleaseStatus }
export interface ReleaseView { id: string; applicationId: string; name: string; plannedDate: string | null; status: ReleaseStatus; createdAt: string }
