import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { renderAt } from "../test/helpers";
import { issueSummary, issueView } from "../test/issues-fixtures";
import { sprintView } from "../test/sprints-fixtures";
import { issuePage, serveSprint } from "../test/sprints-serve";
import { SprintPage } from "./SprintPage";

const route = [
  { path: "/w/:ws/projects/:projectId/sprints/:id", element: <SprintPage /> },
  { path: "/w/:ws/projects/:projectId/issues/:id", element: <div>issue page</div> },
];
const open = (path = "/w/w1/projects/p1/sprints/s1") => renderAt(path, route);
const issueQueries = (calls: { method: string; path: string }[], part: string) => calls.filter((c) => c.method === "GET" && c.path.startsWith("/projects/p1/issues") && c.path.includes(part));
const column = (name: string) => screen.getByRole("region", { name });
const manyCandidates = (n: number) => Array.from({ length: n }, (_, i) => issueSummary({ id: `m${i}`, number: 100 + i, title: `Many ${i}`, status: "backlog" }));

describe("the sprint page: the issues by status", () => {
  it("puts each issue in the column of its status, with its facts and a link", async () => {
    const calls = serveSprint();
    open();
    await screen.findByText("Pay with a card");
    expect(within(column("In progress")).getByText("Pay with a card")).toBeInTheDocument();
    expect(within(column("Ready")).getByText("Refund an order")).toBeInTheDocument();
    expect(within(column("Done")).getByText("Print a receipt")).toBeInTheDocument();
    expect(within(column("Backlog")).getByText("None.")).toBeInTheDocument();
    expect(within(column("Cancelled")).getByText("None.")).toBeInTheDocument();
    const second = screen.getByText("Refund an order").closest("li")!;
    expect(within(second).getByText("#2")).toBeInTheDocument();
    expect(within(second).getByText("Bug")).toBeInTheDocument();
    expect(within(second).getByText("Estimate: 2.5 h")).toBeInTheDocument();
    expect(within(second).getByText("Unassigned")).toBeInTheDocument();
    expect(within(screen.getByText("Pay with a card").closest("li")!).getByText("Deniz")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Pay with a card" })).toHaveAttribute("href", "/w/w1/projects/p1/issues/i1");
    expect(issueQueries(calls, "sprint=s1")[0]?.path).toBe("/projects/p1/issues?sprint=s1&limit=25");
  });

  it("says so when the sprint has no issues", async () => {
    serveSprint({ "GET /projects/p1/issues": issuePage([]) });
    open();
    expect(await screen.findByText("No issues in this sprint yet.")).toBeInTheDocument();
    expect(await screen.findByText("The backlog has no more issues for this sprint.")).toBeInTheDocument();
  });

  it("loads more of the sprint's issues with the cursor", async () => {
    const calls = serveSprint({
      "GET /projects/p1/issues": (c) => c.path.includes("cursor=c1") ? issuePage([issueSummary({ id: "i4", number: 4, title: "Gift wrap", status: "ready", sprintId: "s1" })])
        : issuePage([issueSummary({ id: "i1", status: "ready", sprintId: "s1" })], c.path.includes("sprint=s1") ? "c1" : null),
    });
    open();
    await userEvent.click((await screen.findAllByRole("button", { name: "Load more" }))[0]!);
    expect(await screen.findByText("Gift wrap")).toBeInTheDocument();
    expect(issueQueries(calls, "cursor=c1")[0]?.path).toBe("/projects/p1/issues?sprint=s1&cursor=c1&limit=25");
  });

  it("shows a refusal when more cannot be loaded", async () => {
    serveSprint({ "GET /projects/p1/issues": (c) => c.path.includes("cursor") ? { status: 500 } : issuePage([issueSummary({ sprintId: "s1" })], c.path.includes("sprint=s1") ? "c1" : null) });
    open();
    await userEvent.click((await screen.findAllByRole("button", { name: "Load more" }))[0]!);
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the sprint page: status and removing", () => {
  it("changes the status of an issue with its own call and reads the sprint again", async () => {
    const calls = serveSprint({ "PUT /issues/i1/status": { status: 204 } });
    open();
    const select = await screen.findByLabelText("Status of #1");
    expect(select).toHaveValue("in_progress");
    await userEvent.selectOptions(select, "done");
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ status: "done" }));
    await waitFor(() => expect(issueQueries(calls, "sprint=s1")).toHaveLength(2));
  });

  it("shows the refusal of a status change", async () => {
    serveSprint({ "PUT /issues/i1/status": { status: 400, body: { errors: { status: ["invalid_value"] } } } });
    open();
    await userEvent.selectOptions(await screen.findByLabelText("Status of #1"), "done");
    expect(await screen.findByText("Pick a value from the list.")).toBeInTheDocument();
  });

  it("removes an issue from the sprint and reads again, also the backlog's candidates", async () => {
    const calls = serveSprint({ "DELETE /sprints/s1/issues/i2": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Remove #2 from the sprint" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/sprints/s1/issues/i2")).toBe(true));
    await waitFor(() => expect(issueQueries(calls, "scope=backlog")).toHaveLength(2));
  });

  it("shows the refusal of a removal", async () => {
    serveSprint({ "DELETE /sprints/s1/issues/i2": { status: 400, body: { errors: { status: ["closed"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Remove #2 from the sprint" }));
    expect(await screen.findByText("This sprint is closed.")).toBeInTheDocument();
  });
});

describe("the sprint page: adding issues from the backlog", () => {
  it("lists the backlog's issues and adds the selected ones", async () => {
    let added = false;
    const calls = serveSprint({
      "POST /sprints/s1/issues": () => { added = true; return { status: 204 }; },
      "GET /projects/p1/issues": (c) => issuePage(c.path.includes("sprint=s1") ? [] : added ? [] : [issueSummary({ id: "b1", number: 11, title: "Gift cards" }), issueSummary({ id: "b2", number: 12, title: "Wish list", type: "task", estimate: null })]),
    });
    open();
    await screen.findByText("Gift cards");
    expect(issueQueries(calls, "scope=backlog")[0]?.path).toBe("/projects/p1/issues?scope=backlog&limit=25");
    expect(screen.getByRole("button", { name: "Add selected (0)" })).toBeDisabled();
    await userEvent.click(screen.getByRole("checkbox", { name: "Select #11" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Select #12" }));
    await userEvent.click(screen.getByRole("button", { name: "Add selected (2)" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ issueIds: ["b1", "b2"] }));
    expect(await screen.findByText("The backlog has no more issues for this sprint.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add selected (0)" })).toBeDisabled();
  });

  it("asks only for the application's issues for an application's sprint", async () => {
    const calls = serveSprint({ "GET /sprints/s1": { body: sprintView({}, { applicationId: "a1" }) } });
    open();
    await screen.findByText("Gift cards");
    expect(issueQueries(calls, "scope=backlog")[0]?.path).toBe("/projects/p1/issues?scope=backlog&application=a1&limit=25");
  });

  it("keeps the selection and shows the refusal when the issue is of another application", async () => {
    serveSprint({ "POST /sprints/s1/issues": { status: 400, body: { errors: { issueIds: ["wrong_scope"] } } } });
    open();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select #11" }));
    await userEvent.click(screen.getByRole("button", { name: "Add selected (1)" }));
    expect(await screen.findByText("This sprint belongs to an application: it takes only that application's issues.")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Select #11" })).toBeChecked();
  });

  it("takes at most 50 at once and lets one be unchecked again", async () => {
    serveSprint({ "GET /projects/p1/issues": (c) => issuePage(c.path.includes("sprint=s1") ? [] : manyCandidates(51)) });
    open();
    await screen.findByText("Many 0");
    for (let i = 0; i < 50; i++) fireEvent.click(screen.getByRole("checkbox", { name: `Select #${100 + i}` }));
    expect(screen.getByRole("button", { name: "Add selected (50)" })).toBeEnabled();
    expect(screen.getByRole("checkbox", { name: "Select #150" })).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: "Select #100" }));
    expect(screen.getByRole("checkbox", { name: "Select #150" })).toBeEnabled();
  }, 30_000); // 50 role queries over 51 rows: over the 5 s default on a loaded CI runner

  it("loads more candidates with the cursor", async () => {
    const calls = serveSprint({
      "GET /projects/p1/issues": (c) => c.path.includes("sprint=s1") ? issuePage([])
        : c.path.includes("cursor=c2") ? issuePage([issueSummary({ id: "b3", number: 13, title: "Coupons" })]) : issuePage([issueSummary({ id: "b1", number: 11, title: "Gift cards" })], "c2"),
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Coupons")).toBeInTheDocument();
    expect(issueQueries(calls, "cursor=c2")[0]?.path).toBe("/projects/p1/issues?scope=backlog&cursor=c2&limit=25");
  });

  it("shows a refusal when more candidates cannot be loaded and when none can be read", async () => {
    serveSprint({ "GET /projects/p1/issues": (c) => c.path.includes("sprint=s1") ? issuePage([]) : c.path.includes("cursor") ? { status: 500 } : issuePage([issueSummary({ id: "b1", number: 11 })], "c2") });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("shows a failed read of the candidates", async () => {
    serveSprint({ "GET /projects/p1/issues": (c) => c.path.includes("sprint=s1") ? issuePage([]) : { status: 500 } });
    open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the sprint page: creating an issue and adding it at once", () => {
  const newIssue = { "POST /projects/p1/issues": { status: 201, body: issueView({}, { id: "n1", number: 20, title: "Coupons" }) }, "POST /sprints/s1/issues": { status: 204 } };

  it("creates the issue, then adds it to the sprint", async () => {
    const calls = serveSprint(newIssue);
    open();
    await userEvent.type(await screen.findByLabelText("Title of the new issue"), "  Coupons ");
    await userEvent.selectOptions(screen.getByLabelText("Type of the new issue"), "bug");
    await userEvent.click(screen.getByRole("button", { name: "Create and add" }));
    await waitFor(() => expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual(["/projects/p1/issues", "/sprints/s1/issues"]));
    expect(calls.find((c) => c.path === "/projects/p1/issues" && c.method === "POST")?.body).toEqual({ title: "Coupons", type: "bug", applicationId: null });
    expect(calls.find((c) => c.path === "/sprints/s1/issues")?.body).toEqual({ issueIds: ["n1"] });
    await waitFor(() => expect(screen.getByLabelText("Title of the new issue")).toHaveValue(""));
    await waitFor(() => expect(issueQueries(calls, "sprint=s1")).toHaveLength(2));
  });

  it("creates the issue in the application of an application's sprint", async () => {
    const calls = serveSprint({ ...newIssue, "GET /sprints/s1": { body: sprintView({}, { applicationId: "a1" }) } });
    open();
    await userEvent.type(await screen.findByLabelText("Title of the new issue"), "Coupons");
    await userEvent.click(screen.getByRole("button", { name: "Create and add" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/projects/p1/issues" && c.method === "POST")?.body).toEqual({ title: "Coupons", type: "story", applicationId: "a1" }));
  });

  it("wants a title and calls nothing without one", async () => {
    const calls = serveSprint(newIssue);
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Create and add" }));
    expect(await screen.findByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
  });

  it("shows the refusal when the issue cannot be created", async () => {
    serveSprint({ "POST /projects/p1/issues": { status: 400, body: { errors: { type: ["invalid_value"] } } } });
    open();
    await userEvent.type(await screen.findByLabelText("Title of the new issue"), "Coupons");
    await userEvent.click(screen.getByRole("button", { name: "Create and add" }));
    expect(await screen.findByText("Pick a value from the list.")).toBeInTheDocument();
  });

  it("shows the refusal when the issue is made but cannot be added, keeps the title and reads the lists again", async () => {
    const calls = serveSprint({ ...newIssue, "POST /sprints/s1/issues": { status: 400, body: { errors: { status: ["closed"] } } } });
    open();
    await userEvent.type(await screen.findByLabelText("Title of the new issue"), "Coupons");
    await userEvent.click(screen.getByRole("button", { name: "Create and add" }));
    expect(await screen.findByText("This sprint is closed.")).toBeInTheDocument();
    expect(screen.getByLabelText("Title of the new issue")).toHaveValue("Coupons");
    await waitFor(() => expect(issueQueries(calls, "scope=backlog").length).toBeGreaterThan(1));
  });
});
