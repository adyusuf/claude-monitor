import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { renderAt } from "../test/helpers";
import { project } from "../test/projects-fixtures";
import { sprintView } from "../test/sprints-fixtures";
import { serveSprint } from "../test/sprints-serve";
import { SprintPage } from "./SprintPage";

const route = [
  { path: "/w/:ws/projects/:projectId/sprints/:id", element: <SprintPage /> },
  { path: "/w/:ws/projects/:projectId/sprints", element: <div>sprint list</div> },
];
const open = (path = "/w/w1/projects/p1/sprints/s1") => renderAt(path, route);
const readOnly = { access: { canEdit: false, canArchive: false, canEditApplications: false } };
const reads = (calls: { method: string; path: string }[]) => calls.filter((c) => c.method === "GET" && c.path === "/sprints/s1").length;
const without = (name: string | RegExp) => expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();

describe("the sprint page: the facts", () => {
  it("shows the number, the state, the dates, the goal, the numbers, the release and the team", async () => {
    serveSprint();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: "Sprint 1 · Launch" })).toBeInTheDocument();
    const facts = screen.getByRole("heading", { level: 2, name: /Sprint 1 · Launch/ }).closest("section")!;
    expect(within(facts).getByText("Planned")).toBeInTheDocument();
    expect(within(facts).getByText("Ship the checkout")).toBeInTheDocument();
    expect(within(facts).getByText("02/03/2026 – 13/03/2026")).toBeInTheDocument();
    expect(within(facts).getByText("20 h")).toBeInTheDocument();
    expect(within(facts).getByText("12 h")).toBeInTheDocument();
    expect(within(facts).getByText("4 h")).toBeInTheDocument();
    expect(within(facts).getByText("1.0.0")).toBeInTheDocument();
    expect(within(facts).getByRole("list", { name: "Team of the sprint" })).toHaveTextContent("Deniz");
    expect(within(facts).getByRole("progressbar")).toHaveAttribute("aria-valuenow", "12");
    expect(screen.getByRole("link", { name: /Sprints/ })).toHaveAttribute("href", "/w/w1/projects/p1/sprints");
  });

  it("shows dashes and notes for what a sprint does not have", async () => {
    serveSprint({ "GET /sprints/s1": { body: sprintView({ team: [] }, { name: null, goal: null, startDate: null, endDate: null, capacity: null }) } });
    open();
    expect(await screen.findByRole("heading", { level: 1, name: "Sprint 1" })).toBeInTheDocument();
    expect(screen.getByText("No dates yet")).toBeInTheDocument();
    expect(screen.getByText("Nobody is in the team.")).toBeInTheDocument();
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
  });

  it("shows only issue counts, no sums and no bar, in a T-shirt project", async () => {
    serveSprint({ "GET /projects/p1": { body: project({ estimationUnit: "t_shirt" }) }, "GET /sprints/s1": { body: sprintView({}, { committed: 0, done: 0 }) } });
    open();
    await screen.findByRole("heading", { level: 1, name: "Sprint 1 · Launch" });
    expect(screen.queryByText("Committed")).not.toBeInTheDocument();
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    expect(screen.getByText("Issues")).toBeInTheDocument();
  });

  it("shows a failed read", async () => {
    serveSprint({ "GET /sprints/s1": { status: 404 } });
    open();
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });

  it("renders text from people as text, never as markup", async () => {
    serveSprint({ "GET /sprints/s1": { body: sprintView({}, { goal: "<img src=x onerror=alert(1)>" }) } });
    open();
    expect(await screen.findByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
  });
});

describe("the sprint page: rights", () => {
  it("gives a reader (canEdit false) no buttons, selects or add panel", async () => {
    serveSprint({ "GET /sprints/s1": { body: sprintView({ canEdit: false }) } });
    open();
    await screen.findByText("Pay with a card");
    expect(screen.queryAllByRole("button")).toHaveLength(0);
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(screen.queryByText("Add issues")).not.toBeInTheDocument();
  });

  it("gives the same to a person without the right to work on applications and to an archived project", async () => {
    serveSprint({ "GET /projects/p1": { body: project(readOnly) } });
    open();
    await screen.findByText("Pay with a card");
    expect(screen.queryAllByRole("button")).toHaveLength(0);
  });

  it("is read-only once closed, with a banner", async () => {
    serveSprint({ "GET /sprints/s1": { body: sprintView({}, { status: "closed" }) } });
    open();
    expect(await screen.findByText("This sprint is closed: it is read-only.")).toBeInTheDocument();
    await screen.findByText("Pay with a card");
    for (const name of ["Edit", "Start", "Close sprint", "Delete"]) without(name);
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(screen.queryByText("Add issues")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Remove/ })).not.toBeInTheDocument();
  });
});

describe("the sprint page: start and delete", () => {
  it("offers Start and Delete on a planned sprint but not Close", async () => {
    serveSprint();
    open();
    await screen.findByText("Pay with a card");
    expect(screen.getByRole("button", { name: "Start" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delete" })).toBeInTheDocument();
    without("Close sprint");
  });

  it("starts the sprint and reads it again", async () => {
    const calls = serveSprint({ "POST /sprints/s1/start": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Start" }));
    await waitFor(() => expect(reads(calls)).toBe(2));
    expect(calls.some((c) => c.method === "POST" && c.path === "/sprints/s1/start")).toBe(true);
  });

  it.each([
    ["status", "sprint_active", "Another sprint of this scope is already active. Close it first."],
    ["startDate", "dates_required", "Set the start and end dates before starting the sprint."],
  ])("shows why it cannot start (%s)", async (field, code, text) => {
    serveSprint({ "POST /sprints/s1/start": { status: 400, body: { errors: { [field]: [code] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Start" }));
    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it("deletes after a confirmation and goes back to the list; declining deletes nothing", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValue(true);
    const calls = serveSprint({ "DELETE /sprints/s1": { status: 204 } });
    open();
    const button = await screen.findByRole("button", { name: "Delete" });
    await userEvent.click(button);
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    await userEvent.click(button);
    expect(await screen.findByText("sprint list")).toBeInTheDocument();
    expect(confirm).toHaveBeenLastCalledWith("Delete sprint 1? Its issues go back to the backlog.");
    expect(reads(calls)).toBe(1);
  });

  it("shows why it cannot be deleted and stays", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serveSprint({ "DELETE /sprints/s1": { status: 400, body: { errors: { status: ["not_planned"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Delete" }));
    expect(await screen.findByText("Only a planned sprint can be deleted.")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "Sprint 1 · Launch" })).toBeInTheDocument();
  });
});

describe("the sprint page: changing the sprint", () => {
  it("opens a form with the sprint's values, the release it has and the team it has", async () => {
    serveSprint();
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Edit" }));
    expect(screen.getByLabelText("Name")).toHaveValue("Launch");
    expect(screen.getByLabelText("Goal")).toHaveValue("Ship the checkout");
    expect(screen.getByLabelText("Start date")).toHaveValue("2026-03-02");
    expect(screen.getByLabelText("End date")).toHaveValue("2026-03-13");
    expect(screen.getByLabelText("Capacity (Hours)")).toHaveValue(20);
    await waitFor(() => expect(screen.getByLabelText("Release")).toHaveValue("r1"));
    expect(screen.getByRole("checkbox", { name: "Deniz" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Örnek Kişi" })).not.toBeChecked();
    expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
  });

  it("keeps a viewer who is in the team and keeps the release even if it is no longer on offer", async () => {
    serveSprint({
      "GET /sprints/s1": { body: sprintView({ team: [{ userId: "u3", displayName: "Gözlemci" }] }, { releaseId: "r7", releaseName: "0.9.0 (old)" }) },
    });
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    expect(await screen.findByRole("checkbox", { name: "Gözlemci" })).toBeChecked();
    expect(within(screen.getByLabelText("Release")).getByRole("option", { name: "0.9.0 (old)" })).toBeInTheDocument();
  });

  it("saves the whole definition and shows the sprint again", async () => {
    const calls = serveSprint({ "PUT /sprints/s1": { status: 204 } });
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    const name = await screen.findByLabelText("Name");
    await userEvent.clear(name);
    await userEvent.type(name, "Relaunch");
    await userEvent.clear(screen.getByLabelText("Capacity (Hours)"));
    await userEvent.click(screen.getByRole("checkbox", { name: "Deniz" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByLabelText("Name")).not.toBeInTheDocument());
    expect(calls.find((c) => c.method === "PUT")?.body).toEqual({
      name: "Relaunch", goal: "Ship the checkout", startDate: "2026-03-02", endDate: "2026-03-13", capacity: null, releaseId: "r1", teamUserIds: [],
    });
    expect(reads(calls)).toBe(2);
  });

  it("checks before asking: the end cannot be before the start", async () => {
    const calls = serveSprint();
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    const end = await screen.findByLabelText("End date");
    await userEvent.clear(end);
    await userEvent.type(end, "2026-03-01");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("The end date cannot be before the start date.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("shows the API's refusal and keeps the form", async () => {
    serveSprint({ "PUT /sprints/s1": { status: 400, body: { errors: { releaseId: ["invalid_release"] } } } });
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
    expect(await screen.findByText("Pick a release of the chosen application.")).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toBeInTheDocument();
  });

  it("cancels back to the facts", async () => {
    serveSprint();
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
  });

  it("does not open the form on a closed sprint", async () => {
    serveSprint({ "GET /sprints/s1": { body: sprintView({}, { status: "closed" }) } });
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    await screen.findByText("This sprint is closed: it is read-only.");
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  });

  it("lists only the releases of the application for an application's sprint", async () => {
    serveSprint({ "GET /sprints/s1": { body: sprintView({}, { applicationId: "a1", releaseId: null }) } });
    open("/w/w1/projects/p1/sprints/s1?edit=1");
    await waitFor(() => expect(within(screen.getByLabelText("Release")).getAllByRole("option")).toHaveLength(2));
    expect(within(screen.getByLabelText("Release")).getAllByRole("option").map((o) => o.textContent)).toEqual(["No release", "1.0.0"]);
  });
});
