// The fixed vocabularies and limits of managed projects and applications (ADR-0011); the API checks the same lists and limits.
import type {
  ApplicationKind, ComponentKind, EnvironmentName, EstimationUnit, ItemType, PriorityScale, ProjectRole, ReleaseStatus, TargetKind, VersioningScheme,
} from "../api/typesProjects";

export const ITEM_TYPES: ItemType[] = ["epic", "story", "task", "bug", "spike"];
export const PRIORITY_SCALES: PriorityScale[] = ["p0_p3", "moscow", "high_medium_low", "one_to_five"];
export const ESTIMATION_UNITS: EstimationUnit[] = ["story_points", "hours", "days", "t_shirt"];
export const PROJECT_ROLES: ProjectRole[] = ["manager", "contributor", "reviewer", "viewer"];
export const APPLICATION_KINDS: ApplicationKind[] = ["web", "mobile", "api", "other"];
export const ENVIRONMENT_NAMES: EnvironmentName[] = ["dev", "test", "prod"];
export const VERSIONING_SCHEMES: VersioningScheme[] = ["semver", "calver", "none"];
export const TARGET_KINDS: TargetKind[] = ["performance", "accessibility", "sla", "security", "other"];
export const COMPONENT_KINDS: ComponentKind[] = ["component", "module"];
export const RELEASE_STATUSES: ReleaseStatus[] = ["planned", "released", "cancelled"];

export const projectLimits = {
  nameMax: 100,
  purposeMax: 2000,
  sponsorMax: 200,
  stakeholdersMax: 50,
  listMax: 30,
  lineMax: 300,
  sprintMin: 1,
  sprintMax: 42,
  budgetMax: 9_999_999_999_999,
  techStackMax: 30,
  techNameMax: 60,
  targetsMax: 20,
  urlMax: 500,
} as const;

/** A workspace role that may create and change projects ("member" and above; a viewer only reads). */
export const canWorkInProjects = (role: string | undefined): boolean => role === "owner" || role === "admin" || role === "member";

/** True for an address that is safe to show as a link: it must start with https:// or http://. Anything else is shown as plain text. */
export const isWebLink = (value: string): boolean => /^https?:\/\//i.test(value.trim());
