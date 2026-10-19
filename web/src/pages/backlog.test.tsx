import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary, threeIssues } from "../test/issues-fixtures";
import { appSummary, application, component, project, release, workspaceMembers } from "../test/projects-fixtures";
import { BacklogPage } from "./BacklogPage";

const route = [
  { path: "/w/:ws/projects/:projectId/backlog", element: <BacklogPage /> },
  { path: "/w/:ws/projects/:projectId/applications/:appId/backlog", element: <BacklogPage /> },
  { path: "/w/:ws/projects/:projectId/issues/:id", element: <div>issue page</div> },
];
const page = (items = threeIssues(), next: string | null = null) => ({ body: { items, next } });

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": { body: { items: [appSummary(), appSummary({ id: "a2", name: "Mobile app" })], next: null } },
  "GET /projects/p1/issues": page(),
  "GET /applications/a1": { body: application() },
  "GET /applications/a1/components": { body: [component()] },
  "GET /applications/a1/releases": { body: [release()] },
  ...over,
});
const open = (path = "/w/w1/projects/p1/backlog") => renderAt(path, route);
const rows = () => screen.getAllByRole("listitem").map((li) => li.querySelector(".issue-title")?.textContent);
const lastIssueQuery = (calls: { method: string; path: string }[]) => calls.filter((c) => c.path.startsWith("/projects/p1/issues")).at(-1)!.path;

describe("the backlog: the list", () => {
  it("shows the issues in the order the API gives, with their facts and the backlog scope", async () => {
    const calls = serve();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: /Backlog/ })).toBeInTheDocument();
    await screen.findByText("Pay with a card");
    expect(rows()).toEqual(["Pay with a card", "Refund an order", "Print a receipt"]);
    const first = screen.getByText("Pay with a card").closest("li")!;
    expect(within(first).getByText("#1")).toBeInTheDocument();
    expect(within(first).getByText("Story")).toBeInTheDocument();
    expect(within(first).getByText("Priority: Must")).toBeInTheDocument();
    expect(within(first).getByText("Estimate: 5 h")).toBeInTheDocument();
    expect(within(first).getByText("Backlog")).toBeInTheDocument();
    expect(within(first).getByText("Deniz")).toBeInTheDocument();
    const second = screen.getByText("Refund an order").closest("li")!;
    expect(within(second).getByText("Unassigned")).toBeInTheDocument();
    expect(within(second).getByText("Ready")).toBeInTheDocument();
    expect(within(second).getByTitle("This issue belongs to an epic")).toBeInTheDocument();
    expect(within(second).getByText("Estimate: 2.5 h")).toBeInTheDocument();
    const third = screen.getByText("Print a receipt").closest("li")!;
    expect(within(third).getByText("Priority: –")).toBeInTheDocument();
    expect(within(third).getByText("Estimate: –")).toBeInTheDocument();
    expect(within(first).getByRole("link", { name: "Open #1" })).toHaveAttribute("href", "/w/w1/projects/p1/issues/i1");
    expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?scope=backlog&limit=25");
    expect(screen.getByRole("link", { name: /Checkout/ })).toHaveAttribute("href", "/w/w1/projects/p1");
  });

  it("switches between backlog, all issues and archived ones", async () => {
    const calls = serve({ "GET /projects/p1/issues": (c) => c.path.includes("archived=true") ? page([issueSummary({ archived: true })]) : page() });
    open();
    await screen.findByText("Refund an order");
    await userEvent.click(screen.getByRole("button", { name: "All issues" }));
    await waitFor(() => expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?limit=25"));
    await userEvent.click(screen.getByRole("button", { name: "Archived" }));
    await waitFor(() => expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?archived=true&limit=25"));
    const row = (await screen.findByText("Pay with a card")).closest("li")!;
    expect(within(row).getByText("Archived")).toBeInTheDocument();
    expect(within(row).getByRole("button", { name: "Restore #1" })).toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: "Move up #1" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Title of the new issue")).not.toBeInTheDocument();
  });

  it("loads more with the cursor and appends", async () => {
    const calls = serve({
      "GET /projects/p1/issues": (c) => c.path.includes("cursor=c1") ? page([issueSummary({ id: "i4", number: 4, title: "Gift cards" })]) : page(threeIssues(), "c1"),
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Gift cards")).toBeInTheDocument();
    expect(rows()).toHaveLength(4);
    expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?scope=backlog&cursor=c1&limit=25");
    expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
  });

  it("shows a refusal when more cannot be loaded", async () => {
    serve({ "GET /projects/p1/issues": (c) => c.path.includes("cursor") ? { status: 500 } : page(threeIssues(), "c1") });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("shows empty states, with a hint only to those who may add", async () => {
    serve({ "GET /projects/p1/issues": page([]) });
    open();
    expect(await screen.findByText("There are no issues here.")).toBeInTheDocument();
    expect(screen.getByText(/Add an issue above/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Archived" }));
    expect(await screen.findByText("There are no archived issues.")).toBeInTheDocument();
  });

  it("shows a failed read of the project, and a failed read of the issues", async () => {
    serve({ "GET /projects/p1": { status: 404 } });
    open();
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });

  it("shows a failed read of the issues", async () => {
    serve({ "GET /projects/p1/issues": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("renders text from people as text, never as markup", async () => {
    serve({ "GET /projects/p1/issues": page([issueSummary({ title: "<img src=x onerror=alert(1)>" })]) });
    open();
    expect(await screen.findByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
  });
});

describe("the backlog: filters", () => {
  it("searches by title after a pause and narrows by type, status and assignee", async () => {
    const calls = serve();
    open();
    await screen.findByText("Refund an order");
    await userEvent.type(screen.getByLabelText("Search issues by title"), "pay");
    await waitFor(() => expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?q=pay&scope=backlog&limit=25"));
    await userEvent.selectOptions(screen.getByLabelText("Type"), "bug");
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("type=bug"));
    await userEvent.selectOptions(screen.getByLabelText("Status"), "ready");
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("status=ready"));
    await userEvent.selectOptions(screen.getByLabelText("Assignee"), "u2");
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("assignee=u2"));
  });

  it("offers only backlog and ready statuses in the backlog and drops another one when going back", async () => {
    const calls = serve();
    open();
    await screen.findByText("Refund an order");
    const status = screen.getByLabelText("Status");
    expect(within(status).getAllByRole("option").map((o) => o.textContent)).toEqual(["Status", "Backlog", "Ready"]);
    await userEvent.click(screen.getByRole("button", { name: "All issues" }));
    expect(within(screen.getByLabelText("Status")).getAllByRole("option")).toHaveLength(7);
    await userEvent.selectOptions(screen.getByLabelText("Status"), "done");
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("status=done"));
    await userEvent.click(screen.getByRole("button", { name: "Backlog" }));
    await waitFor(() => expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?scope=backlog&limit=25"));
    await userEvent.selectOptions(screen.getByLabelText("Status"), "ready");
    await userEvent.click(screen.getByRole("button", { name: "All issues" }));
    await userEvent.click(screen.getByRole("button", { name: "Backlog" }));
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("status=ready"));
  });

  it("fills component and release from the chosen application and clears them when it changes", async () => {
    const calls = serve({ "GET /applications/a2/components": { body: [] }, "GET /applications/a2/releases": { body: [] } });
    open();
    await screen.findByText("Refund an order");
    expect(screen.getByLabelText("Component")).toBeDisabled();
    expect(screen.getByLabelText("Release")).toBeDisabled();
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a1");
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("application=a1"));
    const component = screen.getByLabelText("Component");
    await waitFor(() => expect(component).toBeEnabled());
    await waitFor(() => expect(within(component).getByRole("option", { name: "Cart" })).toBeInTheDocument());
    await userEvent.selectOptions(component, "c1");
    await userEvent.selectOptions(screen.getByLabelText("Release"), "r1");
    await waitFor(() => expect(lastIssueQuery(calls)).toContain("component=c1&release=r1"));
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a2");
    await waitFor(() => expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?application=a2&scope=backlog&limit=25"));
  });

  it("shows a failed read of the components", async () => {
    serve({ "GET /applications/a1/components": { status: 500 } });
    open();
    await screen.findByText("Refund an order");
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a1");
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("keeps the newest answer when the filters change quickly", async () => {
    let release: (() => void) | null = null;
    const slow = new Promise<void>((resolve) => { release = resolve; });
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
      const url = String(input);
      if (url.includes("/issues") && url.includes("type=bug")) {
        await slow;
        return new Response(JSON.stringify({ items: [issueSummary({ id: "old", title: "Old answer" })], next: null }), { status: 200 });
      }
      return new Response(JSON.stringify(url.includes("/issues") ? { items: threeIssues(), next: null } : url.endsWith("/projects/p1") ? project() : url.includes("members") ? workspaceMembers : { items: [], next: null }), { status: 200 });
    });
    open();
    await screen.findByText("Refund an order");
    await userEvent.selectOptions(screen.getByLabelText("Type"), "bug");
    await userEvent.selectOptions(screen.getByLabelText("Type"), "story");
    release!();
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByText("Old answer")).not.toBeInTheDocument();
  });
});
