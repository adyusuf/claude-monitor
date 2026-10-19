import { mockApi } from "./helpers";
import { ME } from "./helpers";
import { appSummary, application, project, release, workspaceMembers } from "./projects-fixtures";
import { candidates, sprintIssues, sprintSummary, sprintView, threeSprints } from "./sprints-fixtures";

type Routes = Parameters<typeof mockApi>[0];

export const issuePage = <T,>(items: T[], next: string | null = null) => ({ body: { items, next } });

/** The calls every sprint page makes; a test overrides what it cares about. The issue list answers by its query: the sprint's issues or the backlog's. */
export const serveSprint = (over: Routes = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": issuePage([appSummary(), appSummary({ id: "a2", name: "Mobile app" })]),
  "GET /applications/a1": { body: application() },
  "GET /applications/a1/releases": { body: [release()] },
  "GET /applications/a2/releases": { body: [release({ id: "r2", applicationId: "a2", name: "2.0.0" })] },
  "GET /sprints/s1": { body: sprintView({}, { releaseId: "r1", releaseName: "1.0.0" }) },
  "GET /projects/p1/issues": (c) => issuePage(c.path.includes("sprint=s1") ? sprintIssues() : candidates()),
  "GET /projects/p1/sprints": issuePage([sprintSummary({ id: "s3", number: 3, name: "Next" })]),
  ...over,
});

export const readOnly = { access: { canEdit: false, canArchive: false, canEditApplications: false } };

/** The calls the sprint list makes: three sprints, two live applications (one with a cancelled release) and an archived one. */
export const serveSprintList = (over: Routes = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": issuePage([appSummary(), appSummary({ id: "a2", name: "Mobile app" }), appSummary({ id: "a3", name: "Old app", status: "archived" })]),
  "GET /projects/p1/sprints": issuePage(threeSprints()),
  "GET /applications/a1": { body: application() },
  "GET /applications/a1/releases": { body: [release(), release({ id: "r9", name: "0.1.0", status: "cancelled" })] },
  "GET /applications/a2/releases": { body: [release({ id: "r2", applicationId: "a2", name: "2.0.0" })] },
  ...over,
});
