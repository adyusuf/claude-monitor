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

describe("the backlog: moving, archiving and restoring", () => {
  it("moves an issue down after its lower neighbour and up after the issue two places above, or to the top", async () => {
    const calls = serve({ "PUT /issues/i1/position": { status: 204 }, "PUT /issues/i3/position": { status: 204 }, "PUT /issues/i2/position": { status: 204 } });
    open();
    await screen.findByText("Refund an order");
    expect(screen.getByRole("button", { name: "Move up #1" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Move down #3" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Move down #1" }));
    await waitFor(() => expect(rows()).toEqual(["Refund an order", "Pay with a card", "Print a receipt"]));
    expect(calls.find((c) => c.path === "/issues/i1/position")?.body).toEqual({ afterIssueId: "i2" });
    await userEvent.click(screen.getByRole("button", { name: "Move up #3" }));
    await waitFor(() => expect(rows()).toEqual(["Refund an order", "Print a receipt", "Pay with a card"]));
    expect(calls.find((c) => c.path === "/issues/i3/position")?.body).toEqual({ afterIssueId: "i2" });
    await userEvent.click(screen.getByRole("button", { name: "Move up #3" }));
    await waitFor(() => expect(rows()).toEqual(["Print a receipt", "Refund an order", "Pay with a card"]));
    expect(calls.filter((c) => c.path === "/issues/i3/position").at(-1)?.body).toEqual({ afterIssueId: null });
  });

  it("leaves the order alone and shows the refusal when a move is refused", async () => {
    serve({ "PUT /issues/i1/position": { status: 403 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Move down #1" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
    expect(rows()).toEqual(["Pay with a card", "Refund an order", "Print a receipt"]);
  });

  it("archives after a confirmation and takes the row out", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValue(true);
    const calls = serve({ "DELETE /issues/i2": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive #2" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Archive #2" }));
    await waitFor(() => expect(rows()).toEqual(["Pay with a card", "Print a receipt"]));
    expect(confirm).toHaveBeenCalledWith("Archive issue #2? It stays in the archived ones and can be restored.");
  });

  it("shows the refusal when archiving is refused and keeps the row", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve({ "DELETE /issues/i1": { status: 403 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive #1" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
    expect(rows()).toHaveLength(3);
  });

  it("restores an archived issue and takes it out of the archived list", async () => {
    const calls = serve({
      "GET /projects/p1/issues": (c) => c.path.includes("archived=true") ? page([issueSummary({ archived: true })]) : page(),
      "POST /issues/i1/restore": { status: 204 },
    });
    open();
    await screen.findByText("Refund an order");
    await userEvent.click(screen.getByRole("button", { name: "Archived" }));
    await userEvent.click(await screen.findByRole("button", { name: "Restore #1" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/issues/i1/restore")).toBe(true));
    expect(await screen.findByText("There are no archived issues.")).toBeInTheDocument();
  });
});

describe("the backlog: quick add", () => {
  it("creates an issue from a title and a type and opens it", async () => {
    const calls = serve({ "POST /projects/p1/issues": { status: 201, body: { issue: issueSummary({ id: "new1" }) } } });
    open();
    await userEvent.type(await screen.findByLabelText("Title of the new issue"), "  Gift cards ");
    const type = screen.getByLabelText("Type of the new issue");
    expect(within(type).getAllByRole("option").map((o) => o.textContent)).toEqual(["Story", "Bug"]);
    await userEvent.selectOptions(type, "bug");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(await screen.findByText("issue page")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ title: "Gift cards", type: "bug", applicationId: null });
  });

  it("refuses a blank title before asking the API, and shows the API's refusal", async () => {
    const calls = serve({ "POST /projects/p1/issues": { status: 400, body: { errors: { type: ["invalid_value"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Add" }));
    expect(await screen.findByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    await userEvent.type(screen.getByLabelText("Title of the new issue"), "Gift cards");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(await screen.findByText("Pick a value from the list.")).toBeInTheDocument();
    expect(screen.queryByText("issue page")).not.toBeInTheDocument();
  });
});

describe("the backlog: rights", () => {
  it("shows no write control to a person who may not work on applications", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) }, "GET /projects/p1/issues": page([]) });
    open();
    expect(await screen.findByText("There are no issues here.")).toBeInTheDocument();
    expect(screen.queryByText(/Add an issue above/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Title of the new issue")).not.toBeInTheDocument();
  });

  it("shows rows without move, archive or restore buttons to a reader", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) } });
    open();
    await screen.findByText("Refund an order");
    expect(screen.getAllByRole("link", { name: /^Open #/ })).toHaveLength(3);
    expect(screen.queryByRole("button", { name: /Move|Archive #|Restore/ })).not.toBeInTheDocument();
  });

  it("is read-only for an archived project", async () => {
    serve({ "GET /projects/p1": { body: project({ status: "archived" }) } });
    open();
    await screen.findByText("Refund an order");
    expect(screen.queryByLabelText("Title of the new issue")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Move down #1" })).not.toBeInTheDocument();
  });
});

describe("the application's own backlog", () => {
  it("fixes the application filter, asks for its issues only and creates issues in it", async () => {
    const calls = serve({ "POST /projects/p1/issues": { status: 201, body: { issue: issueSummary({ id: "new2" }) } } });
    open("/w/w1/projects/p1/applications/a1/backlog");
    expect(await screen.findByRole("heading", { level: 1, name: /Shop site/ })).toBeInTheDocument();
    await screen.findByText("Refund an order");
    expect(lastIssueQuery(calls)).toBe("/projects/p1/issues?application=a1&scope=backlog&limit=25");
    const application = screen.getByLabelText("Application");
    expect(application).toBeDisabled();
    expect(application).toHaveValue("a1");
    expect(screen.getByRole("link", { name: /Shop site/ })).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1");
    await userEvent.type(screen.getByLabelText("Title of the new issue"), "Cart badge");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    await screen.findByText("issue page");
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ title: "Cart badge", type: "story", applicationId: "a1" });
  });
});
