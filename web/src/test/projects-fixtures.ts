import type { Me, Member } from "../api/types";
import type {
  ApplicationSummary, ApplicationView, ComponentView, ProjectMemberView, ProjectSummary, ProjectView, ReleaseView,
} from "../api/typesProjects";
import { ME } from "./helpers";

export const summary = (over: Partial<ProjectSummary> = {}): ProjectSummary => ({
  id: "p1", name: "Checkout", purpose: "Sell things", ownerUserId: "u1", ownerName: "Örnek Kişi", startDate: "2026-01-05", endDate: "2026-12-31",
  status: "active", applicationCount: 2, createdAt: "2026-01-01T10:00:00Z", sourceConnectionId: null, ...over,
});

export const project = (over: Partial<ProjectView> = {}): ProjectView => ({
  id: "p1", workspaceId: "w1", name: "Checkout", purpose: "Sell things", ownerUserId: "u1", ownerName: "Örnek Kişi", sponsor: "Acme board",
  stakeholders: [{ name: "Dana", role: "Finance" }], budgetAmount: 1500, budgetCurrency: "EUR", startDate: "2026-01-05", endDate: "2026-12-31",
  itemTypes: ["story", "bug"], priorityScale: "moscow", estimationUnit: "hours", sprintLengthDays: 10, definitionOfReady: ["Has criteria"],
  definitionOfDone: ["Reviewed", "Tested"], status: "active", createdAt: "2026-01-01T10:00:00Z", updatedAt: "2026-01-02T10:00:00Z",
  access: { canEdit: true, canArchive: true, canEditApplications: true }, sourceConnectionId: null, ...over,
});

export const appSummary = (over: Partial<ApplicationSummary> = {}): ApplicationSummary => ({
  id: "a1", projectId: "p1", name: "Shop site", description: null, kind: "web", techStack: ["React", "TypeScript"], status: "active",
  createdAt: "2026-01-03T10:00:00Z", ...over,
});

export const application = (over: Partial<ApplicationView> = {}): ApplicationView => ({
  id: "a1", projectId: "p1", workspaceId: "w1", name: "Shop site", description: "The storefront", kind: "web", repoUrl: "https://git.example.org/shop",
  environments: [{ name: "dev", url: "http://dev.example.org" }, { name: "prod", url: "javascript:alert(1)" }], techStack: ["React"],
  versioningScheme: "semver", techOwnerUserId: "u1", techOwnerName: "Örnek Kişi",
  nonFunctionalTargets: [{ kind: "performance", target: "Answers within 300 ms" }], status: "active", createdAt: "2026-01-03T10:00:00Z",
  updatedAt: "2026-01-04T10:00:00Z", canEdit: true, ...over,
});

export const component = (over: Partial<ComponentView> = {}): ComponentView => ({
  id: "c1", applicationId: "a1", name: "Cart", kind: "component", status: "active", createdAt: "2026-01-03T10:00:00Z", ...over,
});

export const release = (over: Partial<ReleaseView> = {}): ReleaseView => ({
  id: "r1", applicationId: "a1", name: "1.0.0", plannedDate: "2026-03-09", status: "planned", createdAt: "2026-01-03T10:00:00Z", ...over,
});

export const workspaceMembers: Member[] = [
  { userId: "u1", displayName: "Örnek Kişi", email: "ayse+test@gmail.com", role: "owner", joinedAt: "2026-01-01T00:00:00Z" },
  { userId: "u2", displayName: "Deniz", email: "deniz+test@gmail.com", role: "member", joinedAt: "2026-01-01T00:00:00Z" },
  { userId: "u3", displayName: "Gözlemci", email: "g+test@gmail.com", role: "viewer", joinedAt: "2026-01-01T00:00:00Z" },
];

export const projectMember = (over: Partial<ProjectMemberView> = {}): ProjectMemberView => ({
  userId: "u2", displayName: "Deniz", email: "deniz+test@gmail.com", role: "contributor", addedAt: "2026-01-02T00:00:00Z", ...over,
});

/** The signed-in user with another role in the first workspace. */
export const meAs = (role: "owner" | "admin" | "member" | "viewer"): Me => ({ ...ME, workspaces: [{ id: "w1", name: "Team", role }] });
