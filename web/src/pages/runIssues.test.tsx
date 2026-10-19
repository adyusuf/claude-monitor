import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary, runIssue } from "../test/issues-fixtures";
import { project, summary } from "../test/projects-fixtures";
import { run } from "../test/remote-fixtures";
import { RunList } from "./RunList";

const failedRun = (over: Partial<WebRunView> = {}) => run({
  id: "r1", status: "failed", exitCode: 1, canDecide: false, kind: "test", testsPassed: 40, testsFailed: 2, testsSkipped: 0, endedAt: "2026-03-05T09:05:00Z", ...over,
});

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /runs/r1/issues": { body: [] },
  "GET /workspaces/w1/projects": { body: { items: [summary(), summary({ id: "p2", name: "Billing" }), summary({ id: "p3", name: "Locked" })], next: null } },
  "GET /projects/p1": { body: project() },
  "GET /projects/p2": { body: project({ id: "p2", name: "Billing", itemTypes: ["story", "task"] }) },
  "GET /projects/p3": { status: 403 },
  "GET /projects/p1/issues": (c) => ({ body: { items: c.path.includes("q=refund") ? [issueSummary({ id: "i2", number: 2, title: "Refund an order" })] : [issueSummary(), issueSummary({ id: "i2", number: 2, title: "Refund an order" })], next: null } }),
  ...over,
});
const open = (runs: WebRunView[]) => renderAt("/machine", [
  { path: "/machine", element: <RunList runs={runs} ws="w1" version={0} hasMore={false} onMore={vi.fn()} onChanged={vi.fn()} /> },
  { path: "/w/:ws/projects/:projectId/issues/:id", element: <div>the new issue page</div> },
]);
const issuesButton = () => screen.getByRole("button", { name: "Issues" });

describe("the issues of a run", () => {
  it("is offered for a finished run only", () => {
    serve();
    open(["pending_approval", "approved", "delivered", "running", "denied", "expired", "cancelled"].map((status, i) => run({ id: `x${i}`, status: status as WebRunView["status"] })));
    expect(screen.queryByRole("button", { name: "Issues" })).not.toBeInTheDocument();
  });

  it.each(["succeeded", "failed", "timed_out"] as const)("is offered for a %s run", (status) => {
    serve();
    open([run({ status })]);
    expect(issuesButton()).toHaveAttribute("aria-expanded", "false");
  });

  it("reads the linked issues only when opened, shows them as links with their state, and closes again", async () => {
    const calls = serve({ "GET /runs/r1/issues": { body: [runIssue(), runIssue({ issueId: "i9", projectId: "p2", number: 9, title: "Print a receipt", status: "done" })] } });
    open([failedRun()]);
    expect(calls.some((c) => c.path.startsWith("/runs/r1/issues"))).toBe(false);
    await userEvent.click(issuesButton());
    expect(issuesButton()).toHaveAttribute("aria-expanded", "true");
    const first = await screen.findByRole("link", { name: "#1 Pay with a card" });
    expect(first).toHaveAttribute("href", "/w/w1/projects/p1/issues/i1");
    expect(screen.getByRole("link", { name: "#9 Print a receipt" })).toHaveAttribute("href", "/w/w1/projects/p2/issues/i9");
    expect(first.closest("li")).toHaveTextContent("Backlog");
    expect(screen.getByText("Done")).toBeInTheDocument();
    await userEvent.click(issuesButton());
    expect(screen.queryByRole("link", { name: "#1 Pay with a card" })).not.toBeInTheDocument();
  });

  it("says when the run is linked to nothing", async () => {
    serve();
    open([failedRun()]);
    await userEvent.click(issuesButton());
    expect(await screen.findByText("This run is not linked to an issue.")).toBeInTheDocument();
  });

  it("shows a failed read as a notice without a spinner", async () => {
    serve({ "GET /runs/r1/issues": { status: 500 } });
    open([failedRun()]);
    await userEvent.click(issuesButton());
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: "Loading…" })).not.toBeInTheDocument();
  });
});

describe("linking a run to an issue", () => {
  const start = async () => {
    open([failedRun()]);
    await userEvent.click(issuesButton());
    await userEvent.click(await screen.findByRole("button", { name: "Link to an issue…" }));
  };
  const chooseProject = async (name: string) => userEvent.selectOptions(await screen.findByLabelText("Project"), screen.getByRole("option", { name }));

  it("picks a project, searches its issues and links the chosen one, then reads the list again", async () => {
    const calls = serve({
      "POST /issues/i2/runs": { status: 204 },
      "GET /runs/r1/issues": () => ({ body: calls.some((c) => c.method === "POST") ? [runIssue({ issueId: "i2", number: 2, title: "Refund an order" })] : [] }),
    });
    await start();
    expect(screen.getByLabelText("Search an issue to link")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Link" })).toBeDisabled();
    await chooseProject("Checkout");
    await waitFor(() => expect(screen.getByRole("option", { name: "#1 Pay with a card" })).toBeInTheDocument());
    await userEvent.type(screen.getByLabelText("Search an issue to link"), "refund");
    await waitFor(() => expect(screen.queryByRole("option", { name: "#1 Pay with a card" })).not.toBeInTheDocument());
    expect(calls.some((c) => c.path.includes("/projects/p1/issues") && c.path.includes("q=refund"))).toBe(true);
    await userEvent.selectOptions(screen.getByLabelText("Issue to link"), screen.getByRole("option", { name: "#2 Refund an order" }));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ runId: "r1" }));
    expect(await screen.findByRole("link", { name: "#2 Refund an order" })).toBeInTheDocument();
    expect(screen.queryByLabelText("Project")).not.toBeInTheDocument();
  });

  it("says when no issue matches the search, and clears the choice when another project is picked", async () => {
    serve({ "GET /projects/p1/issues": { body: { items: [], next: null } } });
    await start();
    await chooseProject("Checkout");
    expect(await screen.findByRole("option", { name: "No issue matches." })).toBeInTheDocument();
    await chooseProject("Billing");
    expect(screen.getByLabelText("Issue to link")).toHaveValue("");
  });

  it("shows a repeat and the API's refusal, and keeps the form", async () => {
    serve({ "POST /issues/i1/runs": { status: 400, body: { errors: { runId: ["duplicate_link"] } } } });
    await start();
    await chooseProject("Checkout");
    await userEvent.selectOptions(screen.getByLabelText("Issue to link"), await screen.findByRole("option", { name: "#1 Pay with a card" }));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(await screen.findByText("This run is already attached to this issue.")).toBeInTheDocument();
    expect(screen.getByLabelText("Project")).toHaveValue("p1");
  });

  it("shows a refusal for a project the person cannot work in", async () => {
    serve({ "POST /issues/i1/runs": { status: 403 } });
    await start();
    await chooseProject("Checkout");
    await userEvent.selectOptions(screen.getByLabelText("Issue to link"), await screen.findByRole("option", { name: "#1 Pay with a card" }));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });

  it("shows a failed issue search", async () => {
    serve({ "GET /projects/p1/issues": { status: 500 } });
    await start();
    await chooseProject("Checkout");
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("says there are no projects", async () => {
    serve({ "GET /workspaces/w1/projects": { body: { items: [], next: null } } });
    await start();
    expect(await screen.findByText("There are no projects yet.")).toBeInTheDocument();
  });

  it("shows a failed project read as a notice", async () => {
    serve({ "GET /workspaces/w1/projects": { status: 500 } });
    await start();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("cancels back to the two actions", async () => {
    serve();
    await start();
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(screen.getByRole("button", { name: "Link to an issue…" })).toBeInTheDocument();
  });
});

describe("creating a bug from a run", () => {
  const start = async (r: WebRunView = failedRun()) => {
    open([r]);
    await userEvent.click(issuesButton());
    await userEvent.click(await screen.findByRole("button", { name: "Create a bug" }));
    return screen.findByLabelText("Title of the bug");
  };

  it.each([
    ["a test run with failures", failedRun({ exitCode: 0 })],
    ["a run with a non-zero exit code", failedRun({ testsFailed: null, testsPassed: null, testsSkipped: null, exitCode: 2 })],
    ["a run whose tests all passed but which exited non-zero", failedRun({ testsFailed: 0, exitCode: 3 })],
  ])("is offered for %s", async (_name, r) => {
    serve();
    open([r]);
    await userEvent.click(issuesButton());
    expect(await screen.findByRole("button", { name: "Create a bug" })).toBeInTheDocument();
  });

  it.each([
    ["a clean run", failedRun({ status: "succeeded", exitCode: 0, testsFailed: 0 })],
    ["a run with no outcome numbers", failedRun({ status: "timed_out", exitCode: null, testsFailed: null })],
  ])("is not offered for %s", async (_name, r) => {
    serve();
    open([r]);
    await userEvent.click(issuesButton());
    await screen.findByRole("button", { name: "Link to an issue…" });
    expect(screen.queryByRole("button", { name: "Create a bug" })).not.toBeInTheDocument();
  });

  it("prefills the title from the host and the failed tests, or from the exit code", async () => {
    serve();
    expect(await start()).toHaveValue("Test run failed on build-box: 2 failed");
  });

  it("prefills the exit-code title for a run without test counts", async () => {
    serve();
    expect(await start(failedRun({ testsFailed: null, exitCode: 2 }))).toHaveValue("Run failed on build-box: exit code 2");
  });

  it("lists only the projects that take bugs", async () => {
    serve();
    await start();
    const select = await screen.findByLabelText("Project");
    expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["–", "Checkout"]);
  });

  it("creates a bug with the edited title and nothing of the run, links the run and opens the new issue", async () => {
    const calls = serve({
      "POST /projects/p1/issues": { status: 201, body: { issue: issueSummary({ id: "new1", number: 7 }) } },
      "POST /issues/new1/runs": { status: 204 },
    });
    const title = await start(failedRun({ argv: ["/usr/bin/secret-tool", "--token=abc"], reason: "private reason" }));
    expect(screen.getByRole("button", { name: "Create the bug" })).toBeDisabled();
    await userEvent.selectOptions(await screen.findByLabelText("Project"), screen.getByRole("option", { name: "Checkout" }));
    await userEvent.clear(title);
    expect(screen.getByRole("button", { name: "Create the bug" })).toBeDisabled();
    await userEvent.type(title, "  Checkout breaks on Safari ");
    await userEvent.click(screen.getByRole("button", { name: "Create the bug" }));
    expect(await screen.findByText("the new issue page")).toBeInTheDocument();
    const create = calls.find((c) => c.method === "POST" && c.path === "/projects/p1/issues");
    expect(create?.body).toEqual({ title: "Checkout breaks on Safari", type: "bug", status: "backlog" });
    expect(calls.find((c) => c.path === "/issues/new1/runs")?.body).toEqual({ runId: "r1" });
    expect(JSON.stringify(calls.map((c) => c.body))).not.toMatch(/secret-tool|token|private reason/);
  });

  it("shows the API's refusal and stays", async () => {
    const calls = serve({ "POST /projects/p1/issues": { status: 403 } });
    await start();
    await userEvent.selectOptions(await screen.findByLabelText("Project"), screen.getByRole("option", { name: "Checkout" }));
    await userEvent.click(screen.getByRole("button", { name: "Create the bug" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
    expect(screen.queryByText("the new issue page")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create the bug" })).toBeEnabled();
    expect(calls.some((c) => c.path.endsWith("/runs") && c.method === "POST")).toBe(false);
  });

  it("keeps a bug that was made but not linked and only links it on a retry", async () => {
    let attempts = 0;
    const calls = serve({
      "POST /projects/p1/issues": { status: 201, body: { issue: issueSummary({ id: "new1", number: 7 }) } },
      "POST /issues/new1/runs": () => (++attempts === 1 ? { status: 400, body: { errors: { status: ["archived"] } } } : { status: 204 }),
    });
    await start();
    await userEvent.selectOptions(await screen.findByLabelText("Project"), screen.getByRole("option", { name: "Checkout" }));
    await userEvent.click(screen.getByRole("button", { name: "Create the bug" }));
    expect(await screen.findByText("This is archived. Restore it first.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create the bug" }));
    expect(await screen.findByText("the new issue page")).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "POST" && c.path === "/projects/p1/issues")).toHaveLength(1);
  });

  it("says when no project takes bugs", async () => {
    serve({ "GET /projects/p1": { body: project({ itemTypes: ["story"] }) } });
    open([failedRun()]);
    await userEvent.click(issuesButton());
    await userEvent.click(await screen.findByRole("button", { name: "Create a bug" }));
    expect(await screen.findByText("No project of this workspace takes bugs.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Title of the bug")).not.toBeInTheDocument();
  });

  it("shows a failed project read as a notice, and cancels back", async () => {
    serve({ "GET /workspaces/w1/projects": { status: 500 } });
    open([failedRun()]);
    await userEvent.click(issuesButton());
    await userEvent.click(await screen.findByRole("button", { name: "Create a bug" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("cancels back to the two actions", async () => {
    serve();
    await start();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.getByRole("button", { name: "Create a bug" })).toBeInTheDocument();
  });
});
