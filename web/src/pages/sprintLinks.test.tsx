import { screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary } from "../test/issues-fixtures";
import { appSummary, application, component, project, projectMember, release, workspaceMembers } from "../test/projects-fixtures";
import { sprintSummary } from "../test/sprints-fixtures";
import { ApplicationPage } from "./ApplicationPage";
import { BacklogPage } from "./BacklogPage";
import { ProjectPage } from "./ProjectPage";

const readOnly = { access: { canEdit: false, canArchive: false, canEditApplications: false } };
const list = (items: unknown[]) => ({ body: { items, next: null } });

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/members": { body: [projectMember()] },
  "GET /projects/p1/applications": list([appSummary()]),
  "GET /projects/p1/sprints": list([]),
  "GET /applications/a1": { body: application() },
  "GET /applications/a1/components": { body: [component()] },
  "GET /applications/a1/releases": { body: [release()] },
  "GET /projects/p1/issues": list([]),
  ...over,
});

describe("the project page: sprints", () => {
  const open = () => renderAt("/w/w1/projects/p1", [{ path: "/w/:ws/projects/:id", element: <ProjectPage /> }]);

  it("links to the sprints and offers to start one", async () => {
    serve();
    open();
    expect(await screen.findByRole("link", { name: "Sprints" })).toHaveAttribute("href", "/w/w1/projects/p1/sprints");
    expect(screen.getByRole("link", { name: "Start a sprint" })).toHaveAttribute("href", "/w/w1/projects/p1/sprints?new=1");
  });

  it("offers no shortcut to a reader but still links to the sprints", async () => {
    serve({ "GET /projects/p1": { body: project(readOnly) } });
    open();
    expect(await screen.findByRole("link", { name: "Sprints" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Start a sprint" })).not.toBeInTheDocument();
  });

  it("offers no shortcut on an archived project", async () => {
    serve({ "GET /projects/p1": { body: project({ status: "archived" }) } });
    open();
    expect(await screen.findByRole("link", { name: "Sprints" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Start a sprint" })).not.toBeInTheDocument();
  });

  it("shows the running sprints with their dates and bar, and nothing when there are none", async () => {
    const calls = serve({ "GET /projects/p1/sprints": list([sprintSummary({ id: "s2", number: 2, name: "Payments", status: "active" })]) });
    open();
    const card = (await screen.findByRole("heading", { level: 2, name: "Active sprints" })).closest("section")!;
    expect(within(card).getByRole("link", { name: "Sprint 2 · Payments" })).toHaveAttribute("href", "/w/w1/projects/p1/sprints/s2");
    expect(within(card).getByText("02/03/2026 – 13/03/2026 · Issues: 3")).toBeInTheDocument();
    expect(within(card).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "12");
    expect(calls.find((c) => c.path.startsWith("/projects/p1/sprints"))?.path).toBe("/projects/p1/sprints?status=active&limit=10");
  });

  it("shows no card when no sprint runs", async () => {
    serve();
    open();
    await screen.findByRole("link", { name: "Sprints" });
    expect(screen.queryByRole("heading", { level: 2, name: "Active sprints" })).not.toBeInTheDocument();
  });

  it("shows a failed read of the running sprints", async () => {
    serve({ "GET /projects/p1/sprints": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the application page: sprints", () => {
  const open = () => renderAt("/w/w1/projects/p1/applications/a1", [{ path: "/w/:ws/projects/:projectId/applications/:id", element: <ApplicationPage /> }]);

  it("links to the application's sprints and offers to start one", async () => {
    serve();
    open();
    expect(await screen.findByRole("link", { name: "Sprints" })).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1/sprints");
    expect(screen.getByRole("link", { name: "Start a sprint" })).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1/sprints?new=1");
  });

  it("offers no shortcut to those who may not change the application", async () => {
    serve({ "GET /applications/a1": { body: application({ canEdit: false }) } });
    open();
    expect(await screen.findByRole("link", { name: "Sprints" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Start a sprint" })).not.toBeInTheDocument();
  });
});

describe("the backlog: sprint badge", () => {
  it("marks an issue that is in a sprint with a link to it, and no one else", async () => {
    serve({ "GET /projects/p1/issues": list([issueSummary({ sprintId: "s1" }), issueSummary({ id: "i2", number: 2, title: "Refund an order" })]) });
    renderAt("/w/w1/projects/p1/backlog", [{ path: "/w/:ws/projects/:projectId/backlog", element: <BacklogPage /> }]);
    const first = (await screen.findByText("Pay with a card")).closest("li")!;
    expect(within(first).getByRole("link", { name: "In a sprint" })).toHaveAttribute("href", "/w/w1/projects/p1/sprints/s1");
    expect(within(screen.getByText("Refund an order").closest("li")!).queryByRole("link", { name: "In a sprint" })).not.toBeInTheDocument();
  });
});
