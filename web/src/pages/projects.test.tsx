import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { meAs, summary, workspaceMembers } from "../test/projects-fixtures";
import { ProjectsPage } from "./ProjectsPage";

const route = [
  { path: "/w/:ws/projects", element: <ProjectsPage /> },
  { path: "/w/:ws/projects/:id", element: <div>project detail</div> },
];
const page = (...items: ReturnType<typeof summary>[]) => ({ body: { items, next: null } });

describe("the projects page", () => {
  it("shows a card per project with its owner, dates (dd/mm/yyyy), applications and an archived badge", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": page(summary(), summary({ id: "p2", name: "Old one", status: "archived", startDate: null, endDate: null, purpose: null })) });
    renderAt("/w/w1/projects", route);
    expect(await screen.findByRole("link", { name: "Checkout" })).toHaveAttribute("href", "/w/w1/projects/p1");
    expect(screen.getByText("Sell things")).toBeInTheDocument();
    expect(screen.getByText("05/01/2026 – 31/12/2026")).toBeInTheDocument();
    expect(screen.getAllByText("Owner: Örnek Kişi")).toHaveLength(2);
    expect(screen.getAllByText("Applications: 2")).toHaveLength(2);
    expect(screen.getByText("Archived")).toBeInTheDocument();
  });

  it("shows an empty state, and a different one when a search finds nothing", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": page() });
    renderAt("/w/w1/projects", route);
    expect(await screen.findByText("There are no projects yet.")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Search projects"), "zzz");
    expect(await screen.findByText("No project matches the search.")).toBeInTheDocument();
  });

  it("searches through the API, shows archived ones on request and loads more with the cursor", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/projects": (call) => call.path.includes("cursor=c1") ? page(summary({ id: "p3", name: "Second page" })) : { body: { items: [summary()], next: "c1" } },
    });
    renderAt("/w/w1/projects", route);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByRole("link", { name: "Second page" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Search projects"), "şiş");
    await waitFor(() => expect(calls.some((c) => c.path.includes("q=%C5%9Fi%C5%9F"))).toBe(true));
    await userEvent.click(screen.getByLabelText("Show archived projects"));
    await waitFor(() => expect(calls.some((c) => c.path.includes("status=all"))).toBe(true));
  });

  it("shows a failed load", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": { status: 500 } });
    renderAt("/w/w1/projects", route);
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("gives a viewer no way to create, edit or archive", async () => {
    mockApi({ "GET /me": { body: meAs("viewer") }, "GET /workspaces/w1/projects": page(summary()) });
    renderAt("/w/w1/projects", route);
    expect(await screen.findByRole("link", { name: "Checkout" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New project" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Edit" })).not.toBeInTheDocument();
  });

  it("lets a member create a project with the form, then opens it", async () => {
    const calls = mockApi({
      "GET /me": { body: meAs("member") },
      "GET /workspaces/w1/projects": page(),
      "GET /workspaces/w1/members": { body: workspaceMembers },
      "POST /workspaces/w1/projects": { status: 201, body: { id: "p9" } },
    });
    renderAt("/w/w1/projects", route);
    await userEvent.click(await screen.findByRole("button", { name: "New project" }));
    await userEvent.type(await screen.findByLabelText("Name"), "  Billing  ");
    await userEvent.type(screen.getByLabelText("Purpose"), "Invoices");
    await userEvent.type(screen.getByLabelText("Sponsor"), "Board");
    await userEvent.click(screen.getByRole("button", { name: "Add a stakeholder" }));
    await userEvent.type(screen.getByLabelText("Stakeholder name 1"), "Dana");
    await userEvent.type(screen.getByLabelText("Stakeholder role 1"), "Finance");
    await userEvent.type(screen.getByLabelText("Budget amount"), "250.5");
    await userEvent.type(screen.getByLabelText("Currency"), "eur");
    await userEvent.type(screen.getByLabelText("Start date"), "2026-02-01");
    await userEvent.type(screen.getByLabelText("End date"), "2026-03-01");
    await userEvent.click(screen.getByLabelText("Spike"));
    await userEvent.selectOptions(screen.getByLabelText("Priority scale"), "moscow");
    await userEvent.selectOptions(screen.getByLabelText("Estimation unit"), "t_shirt");
    await userEvent.clear(screen.getByLabelText("Sprint length (days)"));
    await userEvent.type(screen.getByLabelText("Sprint length (days)"), "7");
    await userEvent.click(screen.getAllByRole("button", { name: "Add a line" })[0]!);
    await userEvent.type(screen.getByLabelText("Definition of Ready 1"), " Has criteria ");
    await userEvent.click(screen.getAllByRole("button", { name: "Add a line" })[1]!);
    await userEvent.click(screen.getByRole("button", { name: "Remove Definition of Done 1" }));
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(await screen.findByText("project detail")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({
      name: "Billing", purpose: "Invoices", ownerUserId: "u1", sponsor: "Board", stakeholders: [{ name: "Dana", role: "Finance" }],
      budgetAmount: 250.5, budgetCurrency: "EUR", startDate: "2026-02-01", endDate: "2026-03-01", itemTypes: ["epic", "story", "task", "bug"],
      priorityScale: "moscow", estimationUnit: "t_shirt", sprintLengthDays: 7, definitionOfReady: ["Has criteria"], definitionOfDone: [],
    });
  });

  it("checks the form before sending and shows what is wrong next to the field", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": page(), "GET /workspaces/w1/members": { body: workspaceMembers } });
    renderAt("/w/w1/projects", route);
    await userEvent.click(await screen.findByRole("button", { name: "New project" }));
    await userEvent.type(await screen.findByLabelText("Budget amount"), "10");
    await userEvent.type(screen.getByLabelText("Start date"), "2026-03-01");
    await userEvent.type(screen.getByLabelText("End date"), "2026-02-01");
    await userEvent.click(screen.getByLabelText("Task"));
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(screen.getByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(screen.getByText("Give the budget amount and its currency together, or neither.")).toBeInTheDocument();
    expect(screen.getByText("The end date cannot be before the start date.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    await userEvent.type(screen.getByLabelText("Currency"), "eu");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(screen.getByText("Use three capital letters, such as EUR.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Budget amount")).not.toBeInTheDocument();
  });

  it("shows the API's refusal of a name that is taken", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1/projects": page(), "GET /workspaces/w1/members": { body: workspaceMembers },
      "POST /workspaces/w1/projects": { status: 400, body: { errors: { name: ["name_taken"] } } },
    });
    renderAt("/w/w1/projects", route);
    await userEvent.click(await screen.findByRole("button", { name: "New project" }));
    await userEvent.type(await screen.findByLabelText("Name"), "Checkout");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(await screen.findByText("That name is already used here. Pick another.")).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toBeInTheDocument();
  });

  it("archives after a confirmation and reloads, restores an archived one, and shows the API's refusal", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let archived = false;
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/projects": () => page(summary({ status: archived ? "archived" : "active" })),
      "DELETE /projects/p1": () => {
        archived = true;
        return { status: 204 };
      },
      "POST /projects/p1/restore": { status: 403 },
    });
    renderAt("/w/w1/projects", route);
    await userEvent.click(await screen.findByRole("button", { name: "Archive" }));
    expect(await screen.findByText("Archived")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "DELETE" && c.path === "/projects/p1")).toBe(true);
    await userEvent.click(screen.getByRole("button", { name: "Restore" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });

  it("does not archive when the confirmation is declined, and Edit goes to the form of the project", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": page(summary()) });
    renderAt("/w/w1/projects", route);
    const card = (await screen.findByRole("article"));
    expect(within(card).getByRole("link", { name: "Edit" })).toHaveAttribute("href", "/w/w1/projects/p1?edit=1");
    await userEvent.click(within(card).getByRole("button", { name: "Archive" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
  });

  it("shows an error when the people for the form cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": page(), "GET /workspaces/w1/members": { status: 403 } });
    renderAt("/w/w1/projects", route);
    await userEvent.click(await screen.findByRole("button", { name: "New project" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });
});
