import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { IssueView } from "../api/typesIssues";
import { ME, mockApi, renderAt } from "../test/helpers";
import { bugView, issueSummary, issueView } from "../test/issues-fixtures";
import { appSummary, component, project, release, workspaceMembers } from "../test/projects-fixtures";
import { IssuePage } from "./IssuePage";

const route = [{ path: "/w/:ws/projects/:projectId/issues/:id", element: <IssuePage /> }];
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
  "GET /applications/a1/components": { body: [component(), component({ id: "c2", name: "Checkout form" })] },
  "GET /applications/a1/releases": { body: [release()] },
  "GET /applications/a2/components": { body: [] },
  "GET /applications/a2/releases": { body: [] },
  "PUT /issues/i1": { status: 204 },
  ...over,
});
const edit = async (over: Parameters<typeof serve>[0] = {}) => {
  const calls = serve(over);
  renderAt("/w/w1/projects/p1/issues/i1?edit=1", route);
  await screen.findByLabelText("Title");
  return calls;
};
const options = (label: string) => within(screen.getByLabelText(label)).getAllByRole("option").map((o) => o.textContent);
const view = (over: Partial<IssueView> = {}, issue: Parameters<typeof issueView>[1] = {}) => ({ body: issueView(over, issue) });

describe("the issue form: fields and the whole replace", () => {
  it("opens with the current values and saves every field with PUT", async () => {
    const calls = await edit();
    expect(screen.getByLabelText("Title")).toHaveValue("Pay with a card");
    expect(screen.getByLabelText("Type")).toHaveValue("story");
    expect(screen.getByLabelText("Priority")).toHaveValue("must");
    expect(screen.getByLabelText("Estimate (Hours)")).toHaveValue(5);
    expect(screen.getByLabelText("Assignee")).toHaveValue("u2");
    expect(screen.getByLabelText("Given 1")).toHaveValue("a full cart");
    expect(screen.getByLabelText("Description")).toHaveAttribute("placeholder", "As a … I want … so that …");
    await userEvent.clear(screen.getByLabelText("Title"));
    await userEvent.type(screen.getByLabelText("Title"), "  Pay by card ");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByLabelText("Title")).not.toBeInTheDocument());
    expect(calls.find((c) => c.method === "PUT")?.body).toEqual({
      title: "Pay by card", type: "story", priority: "must", estimate: "5", status: "backlog", assigneeUserId: "u2", parentId: null, applicationId: null,
      componentId: null, releaseId: null, description: "As a buyer I want to pay so that I own it",
      acceptanceCriteria: [{ given: "a full cart", when: "I pay", then: "I get a receipt" }], technicalDesign: "Use the provider SDK",
      bugEnvironment: null, reproSteps: null, expectedResult: null, actualResult: null, severity: null,
    });
  });

  it("sends an empty string to clear the priority and the estimate, and null for the cleared texts", async () => {
    const calls = await edit();
    await userEvent.selectOptions(screen.getByLabelText("Priority"), "");
    await userEvent.clear(screen.getByLabelText("Estimate (Hours)"));
    await userEvent.clear(screen.getByLabelText("Description"));
    await userEvent.selectOptions(screen.getByLabelText("Assignee"), "");
    await userEvent.selectOptions(screen.getByLabelText("Status"), "in_review");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ priority: "", estimate: "", description: null, assigneeUserId: null, status: "in_review" });
  });

  it("closes with Cancel and keeps the issue as it was", async () => {
    const calls = await edit();
    await userEvent.type(screen.getByLabelText("Title"), " more");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Use the provider SDK")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("offers assignees who can work, and keeps the current one even when they are only a viewer now", async () => {
    await edit();
    expect(options("Assignee")).toEqual(["–", "Örnek Kişi", "Deniz"]);
  });

  it("keeps a viewer who is the current assignee among the choices", async () => {
    await edit({ "GET /issues/i1": view({}, { assigneeUserId: "u3", assigneeName: "Gözlemci" }) });
    expect(options("Assignee")).toEqual(["–", "Örnek Kişi", "Deniz", "Gözlemci"]);
  });
});

describe("the issue form: type, priority and estimate", () => {
  it("limits the type to the project's item types", async () => {
    await edit();
    expect(options("Type")).toEqual(["Story", "Bug"]);
  });

  it("keeps the current type among the choices when the project no longer enables it", async () => {
    await edit({ "GET /issues/i1": view({}, { type: "spike" }) });
    expect(options("Type")).toEqual(["Spike", "Story", "Bug"]);
  });

  it.each([
    ["p0_p3", ["No priority", "P0", "P1", "P2", "P3"]],
    ["moscow", ["No priority", "Must", "Should", "Could", "Won't"]],
    ["high_medium_low", ["No priority", "High", "Medium", "Low"]],
    ["one_to_five", ["No priority", "1", "2", "3", "4", "5"]],
  ] as const)("offers the values of the %s scale", async (priorityScale, expected) => {
    await edit({ "GET /projects/p1": { body: project({ priorityScale }) }, "GET /issues/i1": view({}, { priority: null }) });
    expect(options("Priority")).toEqual(expected);
  });

  it("keeps a current priority that is not on the scale", async () => {
    await edit({ "GET /issues/i1": view({}, { priority: "p1" }) });
    expect(options("Priority")).toEqual(["No priority", "p1", "Must", "Should", "Could", "Won't"].map((x) => x === "p1" ? "P1" : x));
  });

  it.each([["story_points", "Story points"], ["hours", "Hours"], ["days", "Days"]] as const)("takes a number for %s", async (estimationUnit, label) => {
    await edit({ "GET /projects/p1": { body: project({ estimationUnit }) } });
    const input = screen.getByLabelText(`Estimate (${label})`);
    expect(input).toHaveAttribute("type", "number");
    expect(input).toHaveAttribute("max", "9999.99");
  });

  it("takes a size for T-shirt sizes and sends it as it is", async () => {
    const calls = await edit({ "GET /projects/p1": { body: project({ estimationUnit: "t_shirt" }) }, "GET /issues/i1": view({}, { estimate: "m" }) });
    const select = screen.getByLabelText("Estimate (T-shirt sizes)");
    expect(options("Estimate (T-shirt sizes)")).toEqual(["–", "XS", "S", "M", "L", "XL"]);
    expect(select).toHaveValue("m");
    await userEvent.selectOptions(select, "xl");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ estimate: "xl" }));
  });

  it("refuses an estimate the unit does not allow, and a blank title, before asking the API", async () => {
    const calls = await edit();
    await userEvent.clear(screen.getByLabelText("Title"));
    await userEvent.clear(screen.getByLabelText("Estimate (Hours)"));
    await userEvent.type(screen.getByLabelText("Estimate (Hours)"), "1.234");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(screen.getByText("Pick a value from the list.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
    await userEvent.clear(screen.getByLabelText("Estimate (Hours)"));
    await userEvent.type(screen.getByLabelText("Estimate (Hours)"), "10000");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Pick a value from the list.")).toBeInTheDocument();
  });

  it("refuses a T-shirt size the list does not have", async () => {
    const calls = await edit({ "GET /projects/p1": { body: project({ estimationUnit: "t_shirt" }) }, "GET /issues/i1": view({}, { estimate: "huge" }) });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Pick a value from the list.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });
});

describe("the issue form: the bug section", () => {
  it("appears only for a bug and is sent only for a bug", async () => {
    const calls = await edit();
    expect(screen.queryByText("Bug details")).not.toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Type"), "bug");
    expect(screen.getByText("Bug details")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Environment"), "Safari 19");
    await userEvent.type(screen.getByLabelText("Steps to reproduce"), "Click twice");
    await userEvent.type(screen.getByLabelText("Expected result"), "One");
    await userEvent.type(screen.getByLabelText("Actual result"), "Two");
    expect(options("Severity")).toEqual(["–", "Critical", "Major", "Minor", "Trivial"]);
    await userEvent.selectOptions(screen.getByLabelText("Severity"), "critical");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({
      type: "bug", bugEnvironment: "Safari 19", reproSteps: "Click twice", expectedResult: "One", actualResult: "Two", severity: "critical",
    });
  });

  it("is dropped from the request when the type changes back from bug", async () => {
    const calls = await edit({ "GET /issues/i1": { body: bugView() } });
    expect(screen.getByLabelText("Environment")).toHaveValue("Safari 19");
    await userEvent.selectOptions(screen.getByLabelText("Type"), "story");
    expect(screen.queryByLabelText("Environment")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ type: "story", bugEnvironment: null, reproSteps: null, expectedResult: null, actualResult: null, severity: null });
  });
});

describe("the issue form: epic, application, component and release", () => {
  it("offers the project's epics except the issue itself, and none for an epic", async () => {
    const calls = await edit({ "GET /projects/p1": { body: project({ itemTypes: ["epic", "story", "bug"] }) } });
    await waitFor(() => expect(options("Epic")).toEqual(["–", "#9 Payments epic"]));
    expect(calls.some((c) => c.path.includes("type=epic"))).toBe(true);
    await userEvent.selectOptions(screen.getByLabelText("Epic"), "e1");
    await userEvent.selectOptions(screen.getByLabelText("Type"), "epic");
    expect(screen.queryByLabelText("Epic")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ type: "epic", parentId: null });
  });

  it("sends the chosen epic for a story", async () => {
    const calls = await edit();
    await waitFor(() => expect(options("Epic")).toHaveLength(2));
    await userEvent.selectOptions(screen.getByLabelText("Epic"), "e1");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ parentId: "e1" }));
  });

  it("fills component and release from the chosen application and clears them when it changes", async () => {
    const calls = await edit();
    expect(screen.getByLabelText("Component")).toBeDisabled();
    expect(screen.getByLabelText("Release")).toBeDisabled();
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a1");
    await waitFor(() => expect(options("Component")).toEqual(["–", "Cart", "Checkout form"]));
    expect(options("Release")).toEqual(["–", "1.0.0"]);
    await userEvent.selectOptions(screen.getByLabelText("Component"), "c2");
    await userEvent.selectOptions(screen.getByLabelText("Release"), "r1");
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a2");
    expect(screen.getByLabelText("Component")).toHaveValue("");
    expect(screen.getByLabelText("Release")).toHaveValue("");
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a1");
    await waitFor(() => expect(options("Component")).toHaveLength(3));
    await userEvent.selectOptions(screen.getByLabelText("Component"), "c2");
    await userEvent.selectOptions(screen.getByLabelText("Release"), "r1");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ applicationId: "a1", componentId: "c2", releaseId: "r1" });
  });

  it("shows a failed read of the application's components", async () => {
    await edit({ "GET /applications/a1/components": { status: 500 } });
    await userEvent.selectOptions(screen.getByLabelText("Application"), "a1");
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("shows a failed read of the epics", async () => {
    await edit({ "GET /projects/p1/issues": { status: 500 } });
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the issue form: criteria, texts and the API's refusals", () => {
  it("adds and removes criteria rows, and drops the empty ones", async () => {
    const calls = await edit();
    await userEvent.click(screen.getByRole("button", { name: "Add a criterion" }));
    await userEvent.click(screen.getByRole("button", { name: "Add a criterion" }));
    await userEvent.type(screen.getByLabelText("Then 2"), "It works");
    expect(screen.getByLabelText("Given 3")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Remove Acceptance criteria 1" }));
    expect(screen.queryByLabelText("Given 3")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ acceptanceCriteria: [{ given: null, when: null, then: "It works" }] });
  });

  it("offers no more than thirty criteria", async () => {
    const rows = Array.from({ length: 30 }, (_, i) => ({ given: `g${i}`, when: null, then: null }));
    await edit({ "GET /issues/i1": view({ acceptanceCriteria: rows }) });
    expect(screen.getByLabelText("Given 30")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add a criterion" })).not.toBeInTheDocument();
  });

  it("refuses texts that are too long and a criterion that is too long", async () => {
    const calls = await edit({ "GET /issues/i1": view({ technicalDesign: "x".repeat(20_001), description: "y".repeat(20_001) }) });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect((await screen.findAllByText("That is too long.")).length).toBe(2);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("refuses long bug texts and a too long criterion line", async () => {
    const calls = await edit({ "GET /issues/i1": view({ ...bugView(), reproSteps: "z".repeat(5001), acceptanceCriteria: [{ given: "g".repeat(501), when: null, then: null }] }, { type: "bug" }) });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("That is too long.")).toBeInTheDocument();
    expect(screen.getByText(/Check the list/)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it.each([
    ["invalid_parent", "The parent must be an epic of this project."],
    ["invalid_application", "Pick an application of this project."],
    ["invalid_component", "Pick a component of the chosen application."],
    ["invalid_release", "Pick a release of the chosen application."],
    ["bug_only", "Only a bug carries the bug details."],
    ["not_a_member", "You are not a member of that workspace."],
    ["archived", "This is archived. Restore it first."],
  ])("keeps the form open and shows %s as a notice", async (code, text) => {
    await edit({ "PUT /issues/i1": { status: 400, body: { errors: { parentId: [code] } } } });
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.getByLabelText("Title")).toBeInTheDocument();
  });
});
