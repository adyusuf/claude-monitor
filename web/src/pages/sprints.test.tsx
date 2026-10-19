import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { renderAt } from "../test/helpers";
import { project } from "../test/projects-fixtures";
import { sprintSummary, threeSprints } from "../test/sprints-fixtures";
import { issuePage, readOnly, serveSprintList } from "../test/sprints-serve";
import { SprintsPage } from "./SprintsPage";

const route = [
  { path: "/w/:ws/projects/:projectId/sprints", element: <SprintsPage /> },
  { path: "/w/:ws/projects/:projectId/applications/:appId/sprints", element: <SprintsPage /> },
  { path: "/w/:ws/projects/:projectId/sprints/:id", element: <div>sprint page</div> },
  { path: "/w/:ws/projects/:projectId", element: <div>project page</div> },
  { path: "/w/:ws/projects/:projectId/applications/:id", element: <div>application page</div> },
];
const page = (items = threeSprints(), next: string | null = null) => issuePage(items, next);
const serve = serveSprintList;
const open = (path = "/w/w1/projects/p1/sprints") => renderAt(path, route);
const lastQuery = (calls: { path: string }[]) => calls.filter((c) => c.path.startsWith("/projects/p1/sprints")).at(-1)!.path;
const cardOf = (title: RegExp | string) => screen.getByRole("link", { name: title }).closest("article")!;

describe("the sprint list", () => {
  it("groups the sprints by status and shows their facts on the card", async () => {
    const calls = serve();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: /Sprints/ })).toBeInTheDocument();
    await screen.findByText("Sprint 2 · Payments");
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual([
      "Active", "Sprint 2 · PaymentsActive", "Planned", "Sprint 3 · PolishPlanned", "Closed", "Sprint 1Closed",
    ]);
    const active = cardOf("Sprint 2 · Payments");
    expect(within(active).getByText("Ship the checkout")).toBeInTheDocument();
    expect(within(active).getByText("02/03/2026 – 13/03/2026")).toBeInTheDocument();
    expect(within(active).getByText("Issues: 3")).toBeInTheDocument();
    expect(within(active).getByText("Release: 1.0.0")).toBeInTheDocument();
    expect(within(active).getByText("12 of 20 h committed")).toBeInTheDocument();
    const bar = within(active).getByRole("progressbar", { name: "Committed estimates against the capacity" });
    expect(bar).toHaveAttribute("aria-valuenow", "12");
    expect(bar).toHaveAttribute("aria-valuemax", "20");
    const planned = cardOf("Sprint 3 · Polish");
    expect(within(planned).getByText("No dates yet")).toBeInTheDocument();
    expect(within(planned).queryByRole("progressbar")).not.toBeInTheDocument();
    expect(within(cardOf("Sprint 1")).getByText("16/02/2026 – 27/02/2026")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Sprint 2 · Payments" })).toHaveAttribute("href", "/w/w1/projects/p1/sprints/s2");
    expect(lastQuery(calls)).toBe("/projects/p1/sprints?limit=25");
    expect(screen.getByRole("link", { name: /Checkout/ })).toHaveAttribute("href", "/w/w1/projects/p1");
  });

  it("shows no bar for T-shirt sizes or when the capacity is missing, and names the application of an application's sprint", async () => {
    serve({
      "GET /projects/p1": { body: project({ estimationUnit: "t_shirt" }) },
      "GET /projects/p1/sprints": page([sprintSummary({ applicationId: "a2" })]),
    });
    open();
    const card = (await screen.findByRole("link", { name: "Sprint 1 · Launch" })).closest("article")!;
    expect(within(card).queryByRole("progressbar")).not.toBeInTheDocument();
    expect(within(card).getByText("Application: Mobile app")).toBeInTheDocument();
  });

  it("filters by status with the status in the query and lists them flat", async () => {
    const calls = serve({ "GET /projects/p1/sprints": (c) => page(c.path.includes("status=planned") ? [threeSprints()[0]!] : threeSprints()) });
    open();
    await screen.findByText("Sprint 2 · Payments");
    await userEvent.click(screen.getByRole("button", { name: "Planned" }));
    await waitFor(() => expect(lastQuery(calls)).toBe("/projects/p1/sprints?status=planned&limit=25"));
    await waitFor(() => expect(screen.queryByText("Sprint 2 · Payments")).not.toBeInTheDocument());
    expect(screen.getByText("Sprint 3 · Polish")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Planned" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(screen.getByRole("button", { name: "All" }));
    expect(await screen.findByText("Sprint 2 · Payments")).toBeInTheDocument();
  });

  it("loads more with the cursor and appends", async () => {
    const calls = serve({
      "GET /projects/p1/sprints": (c) => c.path.includes("cursor=c1") ? page([sprintSummary({ id: "s0", number: 0, name: "Zero", status: "closed" })]) : page(threeSprints(), "c1"),
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Sprint 0 · Zero")).toBeInTheDocument();
    expect(lastQuery(calls)).toBe("/projects/p1/sprints?cursor=c1&limit=25");
    expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
  });

  it("shows a refusal when more cannot be loaded", async () => {
    serve({ "GET /projects/p1/sprints": (c) => c.path.includes("cursor") ? { status: 500 } : page(threeSprints(), "c1") });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("shows the empty states, with the hint only to those who may add", async () => {
    serve({ "GET /projects/p1/sprints": page([]) });
    open();
    expect(await screen.findByText("There are no sprints yet.")).toBeInTheDocument();
    expect(screen.getByText(/create a sprint, then add issues/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Closed" }));
    expect(await screen.findByText("No sprint has this status.")).toBeInTheDocument();
  });

  it("shows no hint to a reader", async () => {
    serve({ "GET /projects/p1": { body: project(readOnly) }, "GET /projects/p1/sprints": page([]) });
    open();
    expect(await screen.findByText("There are no sprints yet.")).toBeInTheDocument();
    expect(screen.queryByText(/create a sprint, then add issues/)).not.toBeInTheDocument();
  });

  it("shows a failed read of the project and of the sprints", async () => {
    serve({ "GET /projects/p1": { status: 404 } });
    open();
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });

  it("shows a failed read of the sprints", async () => {
    serve({ "GET /projects/p1/sprints": { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("renders text from people as text, never as markup", async () => {
    serve({ "GET /projects/p1/sprints": page([sprintSummary({ goal: "<img src=x onerror=alert(1)>" })]) });
    open();
    expect(await screen.findByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
  });
});

describe("the sprint list: rights and the actions of a card", () => {
  it("gives a reader no buttons", async () => {
    serve({ "GET /projects/p1": { body: project(readOnly) } });
    open();
    await screen.findByText("Sprint 3 · Polish");
    expect(screen.queryByRole("button", { name: "New sprint" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Start / })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Delete / })).not.toBeInTheDocument();
  });

  it("gives an archived project no buttons either", async () => {
    serve({ "GET /projects/p1": { body: project({ status: "archived" }) } });
    open();
    await screen.findByText("Sprint 3 · Polish");
    expect(screen.queryByRole("button", { name: "New sprint" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Start / })).not.toBeInTheDocument();
  });

  it("offers Start and Delete on a planned sprint only", async () => {
    serve();
    open();
    await screen.findByText("Sprint 3 · Polish");
    expect(screen.getAllByRole("button", { name: /^Start / })).toHaveLength(1);
    expect(within(cardOf("Sprint 3 · Polish")).getByRole("button", { name: "Delete Sprint 3 · Polish" })).toBeInTheDocument();
    expect(within(cardOf("Sprint 2 · Payments")).queryByRole("button")).not.toBeInTheDocument();
  });

  it("starts a sprint and reads the list again", async () => {
    const calls = serve({ "POST /sprints/s3/start": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Start Sprint 3 · Polish" }));
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith("/projects/p1/sprints"))).toHaveLength(2));
    expect(calls.some((c) => c.method === "POST" && c.path === "/sprints/s3/start")).toBe(true);
  });

  it("shows why a sprint cannot start", async () => {
    serve({ "POST /sprints/s3/start": { status: 400, body: { errors: { status: ["sprint_active"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Start Sprint 3 · Polish" }));
    expect(await screen.findByText("Another sprint of this scope is already active. Close it first.")).toBeInTheDocument();
  });

  it("deletes after a confirmation and not when it is declined", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValue(true);
    const calls = serve({ "DELETE /sprints/s3": { status: 204 } });
    open();
    const button = await screen.findByRole("button", { name: "Delete Sprint 3 · Polish" });
    await userEvent.click(button);
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    await userEvent.click(button);
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/sprints/s3")).toBe(true));
    expect(confirm).toHaveBeenLastCalledWith("Delete sprint 3? Its issues go back to the backlog.");
  });

  it("shows why a sprint cannot be deleted", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve({ "DELETE /sprints/s3": { status: 400, body: { errors: { status: ["not_planned"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Delete Sprint 3 · Polish" }));
    expect(await screen.findByText("Only a planned sprint can be deleted.")).toBeInTheDocument();
  });
});
