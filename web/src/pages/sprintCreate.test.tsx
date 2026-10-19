import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderAt } from "../test/helpers";
import { project } from "../test/projects-fixtures";
import { sprintView } from "../test/sprints-fixtures";
import { readOnly, serveSprintList as serve } from "../test/sprints-serve";
import { SprintsPage } from "./SprintsPage";

const route = [
  { path: "/w/:ws/projects/:projectId/sprints", element: <SprintsPage /> },
  { path: "/w/:ws/projects/:projectId/applications/:appId/sprints", element: <SprintsPage /> },
  { path: "/w/:ws/projects/:projectId/sprints/:id", element: <div>sprint page</div> },
];
const open = (path = "/w/w1/projects/p1/sprints") => renderAt(path, route);
const fixToday = () => vi.useFakeTimers({ toFake: ["Date"], now: new Date(2026, 2, 2, 10, 0, 0) });

afterEach(() => vi.useRealTimers());

/** Sets the value of a controlled field the way a browser edit does (a date input cannot be typed into reliably). */
function fireChange(element: HTMLElement, value: string) {
  const proto = element instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(proto, "value")?.set?.call(element, value);
  element.dispatchEvent(new Event("input", { bubbles: true }));
}

describe("the sprint list: a new sprint", () => {
  it("opens a form whose end date is the start date plus the project's sprint length, and follows the start date", async () => {
    fixToday();
    serve();
    open();
    await userEvent.click(await screen.findByRole("button", { name: "New sprint" }));
    expect(screen.getByLabelText("Start date")).toHaveValue("2026-03-02");
    expect(screen.getByLabelText("End date")).toHaveValue("2026-03-12");
    expect(screen.getByLabelText("Capacity (Hours)")).toBeInTheDocument();
    fireChange(screen.getByLabelText("Start date"), "2026-03-09");
    expect(screen.getByLabelText("End date")).toHaveValue("2026-03-19");
    fireChange(screen.getByLabelText("End date"), "2026-03-20");
    fireChange(screen.getByLabelText("Start date"), "2026-03-10");
    expect(screen.getByLabelText("End date")).toHaveValue("2026-03-20");
  });

  it("asks for a capacity without a unit in a T-shirt project", async () => {
    serve({ "GET /projects/p1": { body: project({ estimationUnit: "t_shirt" }) } });
    open("/w/w1/projects/p1/sprints?new=1");
    expect(await screen.findByLabelText("Capacity")).toBeInTheDocument();
  });

  it("lists the releases of every live application and the members who can work", async () => {
    serve();
    open();
    await userEvent.click(await screen.findByRole("button", { name: "New sprint" }));
    await waitFor(() => expect(within(screen.getByLabelText("Release")).getAllByRole("option")).toHaveLength(3));
    expect(within(screen.getByLabelText("Release")).getAllByRole("option").map((o) => o.textContent)).toEqual(["No release", "Shop site · 1.0.0", "Mobile app · 2.0.0"]);
    expect(screen.getByRole("checkbox", { name: "Örnek Kişi" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Deniz" })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Gözlemci" })).not.toBeInTheDocument();
  });

  it("creates the sprint and opens it", async () => {
    fixToday();
    const calls = serve({ "POST /projects/p1/sprints": { status: 201, body: sprintView({}, { id: "s9" }) } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "New sprint" }));
    await userEvent.type(screen.getByLabelText("Name"), "  Spring  ");
    await userEvent.type(screen.getByLabelText("Goal"), "Pay by card");
    await userEvent.type(screen.getByLabelText("Capacity (Hours)"), "20");
    await waitFor(() => expect(within(screen.getByLabelText("Release")).getAllByRole("option")).toHaveLength(3));
    await userEvent.selectOptions(screen.getByLabelText("Release"), "r1");
    await userEvent.click(screen.getByRole("checkbox", { name: "Deniz" }));
    await userEvent.click(screen.getByRole("button", { name: "Create sprint" }));
    expect(await screen.findByText("sprint page")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({
      name: "Spring", goal: "Pay by card", startDate: "2026-03-02", endDate: "2026-03-12", capacity: 20, applicationId: null, releaseId: "r1", teamUserIds: ["u2"],
    });
  });

  it("opens the form at once for the shortcut and closes it again", async () => {
    serve();
    open("/w/w1/projects/p1/sprints?new=1");
    expect(await screen.findByLabelText("Start date")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByLabelText("Start date")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New sprint" })).toBeInTheDocument();
  });

  it("does not open the form for a reader", async () => {
    serve({ "GET /projects/p1": { body: project(readOnly) } });
    open("/w/w1/projects/p1/sprints?new=1");
    await screen.findByText("Sprint 3 · Polish");
    expect(screen.queryByLabelText("Start date")).not.toBeInTheDocument();
  });

  it("checks the dates, the capacity and the lengths before asking the API", async () => {
    fixToday();
    const calls = serve();
    open("/w/w1/projects/p1/sprints?new=1");
    fireChange(await screen.findByLabelText("End date"), "2026-03-01");
    await userEvent.type(screen.getByLabelText("Capacity (Hours)"), "-3");
    await userEvent.click(screen.getByRole("button", { name: "Create sprint" }));
    expect(await screen.findByText("The end date cannot be before the start date.")).toBeInTheDocument();
    expect(screen.getByText("That value is out of range.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    fireChange(screen.getByLabelText("Name"), "x".repeat(101));
    fireChange(screen.getByLabelText("Goal"), "y".repeat(1001));
    await userEvent.click(screen.getByRole("button", { name: "Create sprint" }));
    expect(screen.getAllByText("That is too long.")).toHaveLength(2);
  });

  it("shows the API's refusal", async () => {
    serve({ "POST /projects/p1/sprints": { status: 400, body: { errors: { teamUserIds: ["not_a_member"] } } } });
    open("/w/w1/projects/p1/sprints?new=1");
    await userEvent.click(await screen.findByRole("button", { name: "Create sprint" }));
    expect(await screen.findByText("You are not a member of that workspace.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create sprint" })).toBeEnabled();
  });

  it("shows a failed read of the releases", async () => {
    serve({ "GET /applications/a1/releases": { status: 500 } });
    open("/w/w1/projects/p1/sprints?new=1");
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the sprint list of an application", () => {
  it("lists only that application's sprints and links back to the application", async () => {
    const calls = serve();
    open("/w/w1/projects/p1/applications/a1/sprints");
    expect(await screen.findByRole("heading", { level: 1, name: /Sprints · Shop site/ })).toBeInTheDocument();
    await screen.findByText("Sprint 2 · Payments");
    expect(calls.filter((c) => c.path.startsWith("/projects/p1/sprints")).at(-1)?.path).toBe("/projects/p1/sprints?application=a1&limit=25");
    expect(screen.getByRole("link", { name: /Shop site/ })).toHaveAttribute("href", "/w/w1/projects/p1/applications/a1");
  });

  it("creates a sprint of the application with only its releases", async () => {
    const calls = serve({ "POST /projects/p1/sprints": { status: 201, body: sprintView({}, { id: "s9", applicationId: "a1" }) } });
    open("/w/w1/projects/p1/applications/a1/sprints?new=1");
    await waitFor(() => expect(within(screen.getByLabelText("Release")).getAllByRole("option")).toHaveLength(2));
    expect(within(screen.getByLabelText("Release")).getAllByRole("option").map((o) => o.textContent)).toEqual(["No release", "1.0.0"]);
    await userEvent.click(screen.getByRole("button", { name: "Create sprint" }));
    expect(await screen.findByText("sprint page")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toMatchObject({ applicationId: "a1", releaseId: null, teamUserIds: [] });
  });
});
