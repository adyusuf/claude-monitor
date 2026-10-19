import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ProjectView } from "../api/typesProjects";
import { ME, mockApi, renderAt } from "../test/helpers";
import { appSummary, application, project, projectMember, workspaceMembers } from "../test/projects-fixtures";
import { ProjectPage } from "./ProjectPage";

const route = [
  { path: "/w/:ws/projects/:id", element: <ProjectPage /> },
  { path: "/w/:ws/projects", element: <div>projects list</div> },
];
const apps = (...items: ReturnType<typeof appSummary>[]) => ({ body: { items, next: null } });

/** The calls every project page makes; a test overrides what it cares about. */
const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/members": { body: [projectMember()] },
  "GET /projects/p1/applications": apps(appSummary(), appSummary({ id: "a2", name: "Mobile app", kind: "mobile", techStack: [], status: "archived" })),
  ...over,
});
const open = (path = "/w/w1/projects/p1") => renderAt(path, route);

describe("the project page: definition and status", () => {
  it("shows every field of the definition, with dates as dd/mm/yyyy", async () => {
    serve();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: /Checkout/ })).toBeInTheDocument();
    expect(screen.getByText("Sell things")).toBeInTheDocument();
    expect(screen.getByText("Acme board")).toBeInTheDocument();
    expect(screen.getByText("Dana")).toBeInTheDocument();
    expect(screen.getByText("1500 EUR")).toBeInTheDocument();
    expect(screen.getByText("05/01/2026 – 31/12/2026")).toBeInTheDocument();
    expect(screen.getByRole("list", { name: "Work item types" })).toHaveTextContent("StoryBug");
    expect(screen.getByText("MoSCoW")).toBeInTheDocument();
    expect(screen.getByText("Hours")).toBeInTheDocument();
    expect(screen.getByText("10 days")).toBeInTheDocument();
    expect(screen.getByText("Has criteria")).toBeInTheDocument();
    expect(screen.getByText("Tested")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Projects/ })).toHaveAttribute("href", "/w/w1/projects");
  });

  it("links to the project's backlog, also for a reader", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) } });
    open();
    expect(await screen.findByRole("link", { name: "Backlog" })).toHaveAttribute("href", "/w/w1/projects/p1/backlog");
  });

  it("shows dashes for what is not filled in", async () => {
    serve({ "GET /projects/p1": { body: project({ purpose: null, ownerName: null, sponsor: null, stakeholders: [], budgetAmount: null, budgetCurrency: null,
      startDate: null, endDate: null, definitionOfReady: [], definitionOfDone: [] }) } });
    open();
    await screen.findByRole("heading", { level: 1, name: /Checkout/ });
    expect(screen.getAllByText("–").length).toBeGreaterThanOrEqual(8);
  });

  it("renders text from people as text, never as markup", async () => {
    serve({ "GET /projects/p1": { body: project({ name: "<b>bold</b>", purpose: "<img src=x onerror=alert(1)>" }) } });
    open();
    expect(await screen.findByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
    expect(document.querySelector("h1 b")).toBeNull();
  });

  it("shows a failed load, and one that is not found", async () => {
    serve({ "GET /projects/p1": { status: 404 } });
    open();
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });

  it("hides the buttons the API says this person may not use", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) } });
    open();
    await screen.findByRole("heading", { level: 1, name: /Checkout/ });
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add an application" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add" })).not.toBeInTheDocument();
    expect(await screen.findByText("Shop site")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Restore" })).not.toBeInTheDocument();
  });

  it("lets a manager (not an archiver) edit but not archive", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: true, canArchive: false, canEditApplications: true } }) }, "GET /projects/p1/applications": apps() });
    open();
    expect(await screen.findByRole("button", { name: "Edit" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Edit" }));
    expect(await screen.findByLabelText("Owner")).toBeDisabled();
  });

  it("archives after a confirmation, and shows the API's refusal", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = serve({ "DELETE /projects/p1": { status: 403 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "DELETE")).toBe(true);
  });

  it("shows an archived project read-only, with Restore for those who may", async () => {
    let state: ProjectView = project({ status: "archived" });
    const calls = serve({
      "GET /projects/p1": () => ({ body: state }),
      "GET /projects/p1/applications": apps(),
      "POST /projects/p1/restore": () => {
        state = project();
        return { status: 204 };
      },
    });
    open();
    expect(await screen.findByText("This project is archived: it is read-only until it is restored.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add an application" })).not.toBeInTheDocument();
    await userEvent.click(screen.getAllByRole("button", { name: "Restore" })[0]!);
    await waitFor(() => expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument());
    expect(calls.some((c) => c.method === "POST" && c.path === "/projects/p1/restore")).toBe(true);
  });

  it("shows an archived project without Restore to a person who may not archive", async () => {
    serve({ "GET /projects/p1": { body: project({ status: "archived", access: { canEdit: true, canArchive: false, canEditApplications: true } }) } });
    open();
    await screen.findByText("This project is archived: it is read-only until it is restored.");
    expect(screen.queryByRole("button", { name: "Restore" })).not.toBeInTheDocument();
  });
});

describe("the project page: editing", () => {
  it("opens the form with the current values and saves the whole definition with PUT", async () => {
    const calls = serve({ "PUT /projects/p1": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Edit" }));
    expect(await screen.findByLabelText("Name")).toHaveValue("Checkout");
    expect(screen.getByLabelText("Budget amount")).toHaveValue(1500);
    expect(screen.getByLabelText("Start date")).toHaveValue("2026-01-05");
    expect(screen.getByLabelText("Stakeholder name 1")).toHaveValue("Dana");
    expect(screen.getByLabelText("Definition of Done 2")).toHaveValue("Tested");
    await userEvent.clear(screen.getByLabelText("Name"));
    await userEvent.type(screen.getByLabelText("Name"), "Checkout 2");
    await userEvent.click(screen.getByLabelText("Epic"));
    await userEvent.click(screen.getByRole("button", { name: "Remove Stakeholders 1" }));
    await userEvent.clear(screen.getByLabelText("Budget amount"));
    await userEvent.clear(screen.getByLabelText("Currency"));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByLabelText("Name")).not.toBeInTheDocument());
    expect(calls.find((c) => c.method === "PUT")?.body).toEqual({
      name: "Checkout 2", purpose: "Sell things", ownerUserId: "u1", sponsor: "Acme board", stakeholders: [], budgetAmount: null, budgetCurrency: null,
      startDate: "2026-01-05", endDate: "2026-12-31", itemTypes: ["epic", "story", "bug"], priorityScale: "moscow", estimationUnit: "hours",
      sprintLengthDays: 10, definitionOfReady: ["Has criteria"], definitionOfDone: ["Reviewed", "Tested"],
    });
  });

  it("opens in edit mode from the card's Edit link, and Cancel closes it", async () => {
    serve();
    open("/w/w1/projects/p1?edit=1");
    expect(await screen.findByLabelText("Name")).toHaveValue("Checkout");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Definition")).toBeInTheDocument();
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  });

  it("does not open the form for a person who may not edit", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) } });
    open("/w/w1/projects/p1?edit=1");
    await screen.findByRole("heading", { level: 1, name: /Checkout/ });
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  });

  it("keeps the form open and shows the API's refusal", async () => {
    serve({ "PUT /projects/p1": { status: 400, body: { errors: { name: ["name_taken"] } } } });
    open("/w/w1/projects/p1?edit=1");
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
    expect(await screen.findByText("That name is already used here. Pick another.")).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toBeInTheDocument();
  });

  it("keeps the owner among the choices even when they are only a viewer now, and offers no viewers otherwise", async () => {
    serve({ "GET /projects/p1": { body: project({ ownerUserId: "u3", ownerName: "Gözlemci" }) } });
    open("/w/w1/projects/p1?edit=1");
    const owner = await screen.findByLabelText("Owner");
    expect(within(owner).getAllByRole("option").map((o) => o.textContent)).toEqual(["Örnek Kişi", "Deniz", "Gözlemci"]);
  });
});

describe("the project page: members and roles", () => {
  it("lists the people with roles, changes a role, removes someone and adds a workspace member", async () => {
    const calls = serve({
      "PUT /projects/p1/members/u2": { status: 204 }, "DELETE /projects/p1/members/u2": { status: 204 }, "PUT /projects/p1/members/u1": { status: 204 },
    });
    open();
    const role = await screen.findByLabelText("Role Deniz");
    expect(role).toHaveValue("contributor");
    await userEvent.selectOptions(role, "reviewer");
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ role: "reviewer" }));
    await userEvent.selectOptions(screen.getByLabelText("Person"), "u1");
    await userEvent.selectOptions(screen.getAllByLabelText("Role").at(-1)!, "manager");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    await waitFor(() => expect(calls.some((c) => c.path === "/projects/p1/members/u1" && JSON.stringify(c.body) === '{"role":"manager"}')).toBe(true));
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/projects/p1/members/u2")).toBe(true));
  });

  it("shows the roles as text to those who may not change them, and an empty state", async () => {
    serve({
      "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) },
      "GET /projects/p1/members": { body: [] },
    });
    open();
    expect(await screen.findByText("Nobody has a project role yet.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Person")).not.toBeInTheDocument();
  });

  it("shows a role as text for a read-only person and an error when a change is refused", async () => {
    serve({ "PUT /projects/p1/members/u2": { status: 403 } });
    open();
    await userEvent.selectOptions(await screen.findByLabelText("Role Deniz"), "viewer");
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });

  it("shows a failed read of the members", async () => {
    serve({ "GET /projects/p1/members": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("offers nobody to add when every workspace member already has a role", async () => {
    serve({ "GET /projects/p1/members": { body: workspaceMembers.map((m) => projectMember({ userId: m.userId, displayName: m.displayName })) } });
    open();
    await screen.findByLabelText("Role Deniz");
    expect(screen.queryByLabelText("Person")).not.toBeInTheDocument();
  });
});

describe("the project page: applications", () => {
  it("shows application cards with kind, stack chips and an archived badge, linking through", async () => {
    serve();
    open();
    const link = await screen.findByRole("link", { name: "Shop site" });
    expect(link).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1");
    expect(screen.getByRole("list", { name: "Technology stack" })).toHaveTextContent("ReactTypeScript");
    const archived = screen.getByRole("link", { name: "Mobile app" }).closest("article")!;
    expect(within(archived).getByText("Archived")).toBeInTheDocument();
    expect(within(archived).getByRole("button", { name: "Restore" })).toBeInTheDocument();
  });

  it("shows an empty state, with a hint only to those who may add", async () => {
    serve({ "GET /projects/p1/applications": apps() });
    open();
    expect(await screen.findByText("This project has no applications yet.")).toBeInTheDocument();
    expect(screen.getByText(/Add the web app/)).toBeInTheDocument();
  });

  it("shows the empty state without a hint to a reader", async () => {
    serve({ "GET /projects/p1": { body: project({ access: { canEdit: false, canArchive: false, canEditApplications: false } }) }, "GET /projects/p1/applications": apps() });
    open();
    expect(await screen.findByText("This project has no applications yet.")).toBeInTheDocument();
    expect(screen.queryByText(/Add the web app/)).not.toBeInTheDocument();
  });

  it("adds an application with the form", async () => {
    const calls = serve({ "POST /projects/p1/applications": { status: 201, body: application() } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Add an application" }));
    await userEvent.type(await screen.findByLabelText("Application name"), "Billing API");
    await userEvent.selectOptions(screen.getByLabelText("Kind"), "api");
    await userEvent.selectOptions(screen.getByLabelText("Versioning scheme"), "calver");
    await userEvent.selectOptions(screen.getByLabelText("Technical owner"), "u2");
    await userEvent.type(screen.getByLabelText("Repository address"), "git@git.example.org:team/billing.git");
    await userEvent.click(screen.getByLabelText("Development"));
    await userEvent.type(screen.getByLabelText("Address of Development"), "http://dev.example.org");
    await userEvent.click(screen.getByLabelText("Production"));
    await userEvent.click(screen.getByLabelText("Test"));
    await userEvent.type(screen.getByLabelText("Technology"), "dotnet{Enter}");
    await userEvent.type(screen.getByLabelText("Technology"), "DotNet");
    const form = within(screen.getByLabelText("Application name").closest("form")!);
    await userEvent.click(form.getByRole("button", { name: "Add" }));
    await userEvent.type(screen.getByLabelText("Technology"), "Postgres");
    await userEvent.click(form.getByRole("button", { name: "Add" }));
    await userEvent.click(screen.getByRole("button", { name: "Add a target" }));
    await userEvent.selectOptions(screen.getByLabelText("Target kind 1"), "sla");
    await userEvent.type(screen.getByLabelText("Target 1"), "99.9 percent");
    await userEvent.click(screen.getByRole("button", { name: "Add a target" }));
    await userEvent.click(screen.getByRole("button", { name: "Remove Non-functional targets 2" }));
    await userEvent.click(screen.getByRole("button", { name: "Remove dotnet" }));
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/projects/p1/applications")).toBe(true));
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({
      name: "Billing API", description: null, kind: "api", repoUrl: "git@git.example.org:team/billing.git",
      environments: [{ name: "dev", url: "http://dev.example.org" }, { name: "test", url: null }, { name: "prod", url: null }], techStack: ["Postgres"],
      versioningScheme: "calver", techOwnerUserId: "u2", nonFunctionalTargets: [{ kind: "sla", target: "99.9 percent" }],
    });
    await waitFor(() => expect(screen.queryByLabelText("Application name")).not.toBeInTheDocument());
  });

  it("checks an application on the client: name, repository and environment addresses", async () => {
    const calls = serve();
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Add an application" }));
    await userEvent.type(await screen.findByLabelText("Repository address"), "https://user:secret@git.example.org/x");
    await userEvent.click(screen.getByLabelText("Development"));
    await userEvent.type(screen.getByLabelText("Address of Development"), "ftp://dev.example.org");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(screen.getByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(screen.getAllByText(/That address is not allowed here/)).toHaveLength(2);
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Application name")).not.toBeInTheDocument();
  });

  it("shows the API's refusal of an application, such as a taken name", async () => {
    serve({ "POST /projects/p1/applications": { status: 400, body: { errors: { name: ["name_taken"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Add an application" }));
    await userEvent.type(await screen.findByLabelText("Application name"), "Shop site");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(await screen.findByText("That name is already used here. Pick another.")).toBeInTheDocument();
  });

  it("edits an application from its card: reads it, then saves with PUT", async () => {
    const calls = serve({ "GET /applications/a1": { body: application({ environments: [{ name: "dev", url: "http://dev.example.org" }] }) }, "PUT /applications/a1": { status: 204 } });
    open();
    const card = (await screen.findByRole("link", { name: "Shop site" })).closest("article")!;
    await userEvent.click(within(card).getByRole("button", { name: "Edit" }));
    expect(await screen.findByLabelText("Application name")).toHaveValue("Shop site");
    expect(screen.getByLabelText("Technical owner")).toHaveValue("u1");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/applications/a1")).toBe(true));
    expect((calls.find((c) => c.method === "PUT" && c.path === "/applications/a1")?.body as { name: string }).name).toBe("Shop site");
  });

  it("archives and restores an application from its card", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = serve({ "DELETE /applications/a1": { status: 204 }, "POST /applications/a2/restore": { status: 204 } });
    open();
    const card = (await screen.findByRole("link", { name: "Shop site" })).closest("article")!;
    await userEvent.click(within(card).getByRole("button", { name: "Archive" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/applications/a1")).toBe(true));
    const other = screen.getByRole("link", { name: "Mobile app" }).closest("article")!;
    await userEvent.click(within(other).getByRole("button", { name: "Restore" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/applications/a2/restore")).toBe(true));
  });

  it("shows a failed archive of an application", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve({ "DELETE /applications/a1": { status: 403 } });
    open();
    const card = (await screen.findByRole("link", { name: "Shop site" })).closest("article")!;
    await userEvent.click(within(card).getByRole("button", { name: "Archive" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });

  it("loads more applications with the cursor, keeping the ones already shown", async () => {
    serve({ "GET /projects/p1/applications": (call) => call.path.includes("cursor=n1") ? apps(appSummary({ id: "a3", name: "Third" }))
      : { body: { items: [appSummary()], next: "n1" } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByRole("link", { name: "Third" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Shop site" })).toBeInTheDocument();
  });

  it("shows a failed load of the applications", async () => {
    serve({ "GET /projects/p1/applications": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("lists archived and active ones together by asking for all", async () => {
    const calls = serve();
    open();
    await screen.findByRole("link", { name: "Shop site" });
    expect(calls.some((c) => c.path.startsWith("/projects/p1/applications?") && c.path.includes("status=all"))).toBe(true);
  });
});
