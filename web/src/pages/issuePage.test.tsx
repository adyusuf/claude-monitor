import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { IssueView } from "../api/typesIssues";
import { ME, mockApi, renderAt } from "../test/helpers";
import { bugView, issueSummary, issueView } from "../test/issues-fixtures";
import { appSummary, component, project, release, workspaceMembers } from "../test/projects-fixtures";
import { IssuePage } from "./IssuePage";

const route = [
  { path: "/w/:ws/projects/:projectId/issues/:id", element: <IssuePage /> },
  { path: "/w/:ws/projects/:projectId/backlog", element: <div>backlog page</div> },
];
const epics = [issueSummary({ id: "e1", number: 9, title: "Payments epic", type: "epic" }), issueSummary({ id: "i1", number: 1, type: "epic", title: "Itself" })];

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /issues/i1": { body: issueView() },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": { body: { items: [appSummary(), appSummary({ id: "a2", name: "Mobile app" })], next: null } },
  "GET /projects/p1/issues": { body: { items: epics, next: null } },
  "GET /issues/i1/test-cases": { body: [] },
  "GET /issues/i1/code-links": { body: [] },
  "GET /issues/i1/runs": { body: [] },
  "GET /applications/a1/components": { body: [component(), component({ id: "c2", name: "Checkout form" })] },
  "GET /applications/a1/releases": { body: [release()] },
  "GET /applications/a2/components": { body: [] },
  "GET /applications/a2/releases": { body: [] },
  ...over,
});
const open = (path = "/w/w1/projects/p1/issues/i1") => renderAt(path, route);
const view = (over: Partial<IssueView> = {}, issue: Parameters<typeof issueView>[1] = {}) => ({ body: issueView(over, issue) });

describe("the issue page: reading", () => {
  it("shows every field with dates as dd/mm/yyyy and the unit of the estimate", async () => {
    serve();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: /#1 Pay with a card/ })).toBeInTheDocument();
    expect(screen.getByText("As a buyer I want to pay so that I own it")).toBeInTheDocument();
    expect(screen.getByText("a full cart")).toBeInTheDocument();
    expect(screen.getByText("I get a receipt")).toBeInTheDocument();
    expect(screen.getByText("Use the provider SDK")).toBeInTheDocument();
    expect(screen.getByText("5 h")).toBeInTheDocument();
    expect(screen.getByText("Must")).toBeInTheDocument();
    expect(screen.getByText("Deniz")).toBeInTheDocument();
    expect(screen.getByText("01/02/2026")).toBeInTheDocument();
    expect(screen.queryByText("Steps to reproduce")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Backlog/ })).toHaveAttribute("href", "/w/w1/projects/p1/backlog");
  });

  it("shows dashes for what is not filled in", async () => {
    serve({ "GET /issues/i1": view({ description: null, technicalDesign: null, acceptanceCriteria: [] }, { priority: null, estimate: null, assigneeName: null, assigneeUserId: null }) });
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.getAllByText("–").length).toBeGreaterThanOrEqual(8);
  });

  it("shows the bug section only for a bug, with the severity", async () => {
    serve({ "GET /issues/i1": { body: bugView() } });
    open();
    expect(await screen.findByText("Click refund twice")).toBeInTheDocument();
    expect(screen.getByText("Safari 19")).toBeInTheDocument();
    expect(screen.getByText("One refund")).toBeInTheDocument();
    expect(screen.getByText("Two refunds")).toBeInTheDocument();
    expect(screen.getByText("Major")).toBeInTheDocument();
  });

  it("links the epic and the application, and names the component and the release", async () => {
    serve({ "GET /issues/i1": view({ parentTitle: "Payments epic" }, { applicationId: "a1", componentId: "c2", releaseId: "r1", parentId: "e1" }) });
    open();
    expect(await screen.findByRole("link", { name: "Payments epic" })).toHaveAttribute("href", "/w/w1/projects/p1/issues/e1");
    expect(screen.getByRole("link", { name: "Shop site" })).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1");
    expect(await screen.findByText("Checkout form")).toBeInTheDocument();
    expect(screen.getByText("1.0.0")).toBeInTheDocument();
  });

  it("renders text from people as text, never as markup", async () => {
    serve({ "GET /issues/i1": view({ description: "<img src=x onerror=alert(1)>" }, { title: "<b>bold</b>" }) });
    open();
    expect(await screen.findByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
    expect(document.querySelector("h1 b")).toBeNull();
  });

  it("shows a failed load", async () => {
    serve({ "GET /issues/i1": { status: 404 } });
    open();
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });
});

describe("the issue page: rights and archive", () => {
  it("shows no write control when the issue says the person may not edit", async () => {
    serve({ "GET /issues/i1": view({ canEdit: false }) });
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add link" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add test case" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Address (https)")).not.toBeInTheDocument();
  });

  it("shows no write control when the project says the person may not work on applications", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) } });
    open("/w/w1/projects/p1/issues/i1?edit=1");
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Title")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add link" })).not.toBeInTheDocument();
  });

  it("archives after a confirmation and shows the archived banner with Restore", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let archived = false;
    const calls = serve({
      "GET /issues/i1": () => view({}, { archived }),
      "DELETE /issues/i1": () => {
        archived = true;
        return { status: 204 };
      },
      "POST /issues/i1/restore": () => {
        archived = false;
        return { status: 204 };
      },
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive" }));
    expect(await screen.findByText("This issue is archived: it is read-only until it is restored.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add test case" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Restore" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument());
    expect(calls.some((c) => c.method === "POST" && c.path === "/issues/i1/restore")).toBe(true);
  });

  it("does not archive without a confirmation, and shows the refusal of an archive", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValue(true);
    const calls = serve({ "DELETE /issues/i1": { status: 403 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Archive" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
    expect(confirm).toHaveBeenCalledTimes(2);
  });

  it("shows an archived issue without Restore to a person who may not edit", async () => {
    serve({ "GET /issues/i1": view({ canEdit: false }, { archived: true }) });
    open();
    await screen.findByText("This issue is archived: it is read-only until it is restored.");
    expect(screen.queryByRole("button", { name: "Restore" })).not.toBeInTheDocument();
  });
});
