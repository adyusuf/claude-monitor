import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { application, component, release, workspaceMembers } from "../test/projects-fixtures";
import { ApplicationPage } from "./ApplicationPage";

const route = [
  { path: "/w/:ws/projects/:projectId/applications/:id", element: <ApplicationPage /> },
  { path: "/w/:ws/projects/:projectId", element: <div>project page</div> },
];

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /applications/a1": { body: application() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /applications/a1/components": { body: [component(), component({ id: "c2", name: "Payments", kind: "module" })] },
  "GET /applications/a1/releases": { body: [release(), release({ id: "r2", name: "0.9.0", plannedDate: null, status: "released" })] },
  ...over,
});
const open = (path = "/w/w1/projects/p1/applications/a1") => renderAt(path, route);

describe("the application page: definition", () => {
  it("links to the application's own backlog", async () => {
    serve();
    open();
    expect(await screen.findByRole("link", { name: "Backlog" })).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1/backlog");
  });

  it("shows every field, with web addresses as links and anything else as text", async () => {
    serve();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: /Shop site/ })).toBeInTheDocument();
    expect(screen.getByText("The storefront")).toBeInTheDocument();
    const repo = screen.getByRole("link", { name: "https://git.example.org/shop" });
    expect(repo).toHaveAttribute("target", "_blank");
    expect(repo).toHaveAttribute("rel", "noopener noreferrer");
    expect(screen.getByRole("link", { name: "http://dev.example.org" })).toHaveAttribute("href", "http://dev.example.org");
    expect(screen.getByText("javascript:alert(1)")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "javascript:alert(1)" })).not.toBeInTheDocument();
    expect(screen.getByText("Semantic (1.2.3)")).toBeInTheDocument();
    expect(screen.getByText("Answers within 300 ms")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Project/ })).toHaveAttribute("href", "/w/w1/projects/p1");
  });

  it("shows an ssh repository address as plain text and dashes for what is missing", async () => {
    serve({ "GET /applications/a1": { body: application({ repoUrl: "git@git.example.org:team/shop.git", description: null, environments: [], techStack: [],
      techOwnerUserId: null, techOwnerName: null, nonFunctionalTargets: [] }) } });
    open();
    expect(await screen.findByText("git@git.example.org:team/shop.git")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "git@git.example.org:team/shop.git" })).not.toBeInTheDocument();
    expect(screen.getAllByText("–").length).toBeGreaterThanOrEqual(5);
  });

  it("shows a failed load", async () => {
    serve({ "GET /applications/a1": { status: 404 } });
    open();
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });

  it("hides every button when the answer says the person may not edit", async () => {
    serve({ "GET /applications/a1": { body: application({ canEdit: false }) } });
    open();
    expect(await screen.findByText("Cart")).toBeInTheDocument();
    for (const name of ["Edit", "Archive", "Rename", "Add", "Cancel release"]) expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();
    expect(screen.getByText("1.0.0")).toBeInTheDocument();
  });

  it("edits the application and saves it with PUT", async () => {
    const calls = serve({ "PUT /applications/a1": { status: 204 }, "GET /applications/a1": { body: application({ environments: [{ name: "dev", url: "http://dev.example.org" }] }) } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Edit" }));
    await userEvent.clear(await screen.findByLabelText("Application name"));
    await userEvent.type(screen.getByLabelText("Application name"), "Shop v2");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByLabelText("Application name")).not.toBeInTheDocument());
    expect(calls.find((c) => c.method === "PUT")?.body).toEqual({
      name: "Shop v2", description: "The storefront", kind: "web", repoUrl: "https://git.example.org/shop", environments: [{ name: "dev", url: "http://dev.example.org" }],
      techStack: ["React"], versioningScheme: "semver", techOwnerUserId: "u1", nonFunctionalTargets: [{ kind: "performance", target: "Answers within 300 ms" }],
    });
  });

  it("shows the API's refusal and keeps the form open", async () => {
    serve({ "PUT /applications/a1": { status: 400, body: { errors: { name: ["name_taken"] } } }, "GET /applications/a1": { body: application({ environments: [] }) } });
    open("/w/w1/projects/p1/applications/a1?edit=1");
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
    expect(await screen.findByText("That name is already used here. Pick another.")).toBeInTheDocument();
  });

  it("archives after a confirmation, and an archived application is read-only with Restore", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let state = application();
    const calls = serve({
      "GET /applications/a1": () => ({ body: state }),
      "DELETE /applications/a1": () => {
        state = application({ status: "archived" });
        return { status: 204 };
      },
      "POST /applications/a1/restore": () => {
        state = application();
        return { status: 204 };
      },
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive" }));
    expect(await screen.findByText("This application is archived: it is read-only until it is restored.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Rename" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Restore" }));
    await waitFor(() => expect(screen.queryByText("This application is archived: it is read-only until it is restored.")).not.toBeInTheDocument());
    expect(screen.getAllByRole("button", { name: "Edit" }).length).toBeGreaterThan(0);
    expect(calls.filter((c) => c.method !== "GET").map((c) => c.path)).toEqual(["/applications/a1", "/applications/a1/restore"]);
  });

  it("shows the refusal of an archive", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve({ "DELETE /applications/a1": { status: 403 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Archive" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });
});

describe("the application page: components and modules", () => {
  it("lists them with their kind, adds one, renames one and archives one", async () => {
    const calls = serve({
      "POST /applications/a1/components": { status: 201, body: component() }, "PUT /components/c1": { status: 204 }, "DELETE /components/c2": { status: 204 },
    });
    open();
    expect(await screen.findByText("Cart")).toBeInTheDocument();
    expect(screen.getByText("Payments")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("New component name"), "Search");
    await userEvent.selectOptions(screen.getByLabelText("New component kind"), "module");
    await userEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ name: "Search", kind: "module" }));
    const row = screen.getByText("Cart").closest("li")!;
    await userEvent.click(within(row).getByRole("button", { name: "Rename" }));
    const name = screen.getByLabelText("Name");
    await userEvent.clear(name);
    await userEvent.type(name, "Basket");
    await userEvent.selectOptions(screen.getByLabelText("Kind"), "module");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ name: "Basket", kind: "module" }));
    const other = screen.getByText("Payments").closest("li")!;
    await userEvent.click(within(other).getByRole("button", { name: "Archive" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/components/c2")).toBe(true));
  });

  it("cancels a rename, refuses an empty name before asking, and shows the API's refusal", async () => {
    const calls = serve({ "POST /applications/a1/components": { status: 400, body: { errors: { name: ["limit_reached"] } } } });
    open();
    const row = (await screen.findByText("Cart")).closest("li")!;
    await userEvent.click(within(row).getByRole("button", { name: "Rename" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
    await userEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    expect(screen.getByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    await userEvent.type(screen.getByLabelText("New component name"), "X");
    await userEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    expect(await screen.findByText("The most that fits here has been reached.")).toBeInTheDocument();
  });

  it("shows an empty state and a failed load", async () => {
    serve({ "GET /applications/a1/components": { body: [] } });
    open();
    expect(await screen.findByText("No components or modules yet.")).toBeInTheDocument();
  });

  it("reports a failed read of the components", async () => {
    serve({ "GET /applications/a1/components": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the application page: release calendar", () => {
  it("lists releases with the planned day as dd/mm/yyyy and their state", async () => {
    serve();
    open();
    expect(await screen.findByText("1.0.0")).toBeInTheDocument();
    expect(screen.getByText(/09\/03\/2026/)).toBeInTheDocument();
    const planned = screen.getByText("1.0.0").closest("li")!;
    const released = screen.getByText("0.9.0").closest("li")!;
    expect(within(planned).getByText("Planned")).toBeInTheDocument();
    expect(within(released).getByText("Released")).toBeInTheDocument();
    expect(within(released).queryByRole("button", { name: "Cancel release" })).not.toBeInTheDocument();
  });

  it("adds, edits and cancels a release", async () => {
    const calls = serve({ "POST /applications/a1/releases": { status: 201, body: release() }, "PUT /releases/r1": { status: 204 }, "DELETE /releases/r1": { status: 204 } });
    open();
    await screen.findByText("1.0.0");
    await userEvent.type(screen.getByLabelText("Release name new release"), "2.0.0");
    await userEvent.type(screen.getByLabelText("Planned date new release"), "2026-06-01");
    await userEvent.selectOptions(screen.getByLabelText("Release state new release"), "planned");
    const add = screen.getAllByRole("button", { name: "Add" }).at(-1)!;
    await userEvent.click(add);
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ name: "2.0.0", plannedDate: "2026-06-01", status: "planned" }));
    const row = screen.getByText("1.0.0").closest("li")!;
    await userEvent.click(within(row).getByRole("button", { name: "Edit" }));
    const name = screen.getByLabelText("Release name 1.0.0");
    await userEvent.clear(name);
    await userEvent.type(name, "1.0.1");
    await userEvent.clear(screen.getByLabelText("Planned date 1.0.0"));
    await userEvent.selectOptions(screen.getByLabelText("Release state 1.0.0"), "released");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ name: "1.0.1", plannedDate: null, status: "released" }));
    await userEvent.click(within(screen.getByText("1.0.0").closest("li")!).getByRole("button", { name: "Cancel release" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/releases/r1")).toBe(true));
  });

  it("refuses an empty name before asking, cancels an edit and shows the API's refusal", async () => {
    const calls = serve({ "DELETE /releases/r1": { status: 400, body: { errors: { status: ["released"] } } } });
    open();
    await screen.findByText("1.0.0");
    await userEvent.click(screen.getAllByRole("button", { name: "Add" }).at(-1)!);
    expect(screen.getByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    const row = screen.getByText("1.0.0").closest("li")!;
    await userEvent.click(within(row).getByRole("button", { name: "Edit" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Release name 1.0.0")).not.toBeInTheDocument();
    await userEvent.click(within(screen.getByText("1.0.0").closest("li")!).getByRole("button", { name: "Cancel release" }));
    expect(await screen.findByText("A release that went out cannot be cancelled.")).toBeInTheDocument();
  });

  it("shows an empty state and a failed load", async () => {
    serve({ "GET /applications/a1/releases": { body: [] } });
    open();
    expect(await screen.findByText("No releases planned.")).toBeInTheDocument();
  });

  it("reports a failed read of the releases", async () => {
    serve({ "GET /applications/a1/releases": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});
