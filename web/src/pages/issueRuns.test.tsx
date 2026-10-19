import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueRun, issueView } from "../test/issues-fixtures";
import { appSummary, project, workspaceMembers } from "../test/projects-fixtures";
import { run } from "../test/remote-fixtures";
import { IssuePage } from "./IssuePage";

const route = [{ path: "/w/:ws/projects/:projectId/issues/:id", element: <IssuePage /> }];

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /issues/i1": { body: issueView() },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": { body: { items: [appSummary()], next: null } },
  "GET /projects/p1/issues": { body: { items: [], next: null } },
  "GET /issues/i1/test-cases": { body: [] },
  "GET /issues/i1/code-links": { body: [] },
  "GET /issues/i1/runs": { body: [] },
  ...over,
});
const open = async () => {
  renderAt("/w/w1/projects/p1/issues/i1", route);
  await screen.findByRole("heading", { level: 1 });
};
const card = () => screen.getByRole("heading", { name: "Test runs" }).closest("section")!;

/** The runs of the workspace: a build, an older test run, a linked one, one still running and one with no kind. */
const workspaceRuns = () => [
  run({ id: "r-build", targetHostname: "host-build", kind: "build", status: "succeeded", createdAt: "2026-03-01T09:00:00Z", endedAt: "2026-03-01T09:10:00Z" }),
  run({ id: "r-old-test", targetHostname: "host-test", kind: "test", status: "failed", createdAt: "2026-02-01T09:00:00Z", endedAt: "2026-02-01T09:10:00Z" }),
  run({ id: "r-linked", targetHostname: "host-linked", kind: "test", status: "succeeded", createdAt: "2026-03-03T09:00:00Z", endedAt: "2026-03-03T09:10:00Z" }),
  run({ id: "r-running", targetHostname: "host-running", kind: "test", status: "running", createdAt: "2026-03-04T09:00:00Z" }),
  run({ id: "r-nokind", targetHostname: "host-nokind", kind: null, status: "timed_out", createdAt: "2026-03-04T10:00:00Z", endedAt: "2026-03-04T10:10:00Z" }),
];
const linked = () => [issueRun({ runId: "r-linked", targetHostname: "host-linked" })];

describe("the test runs card: reading", () => {
  it("shows a run with its kind, state, exit code, test counts, host and end date", async () => {
    serve({ "GET /issues/i1/runs": { body: [issueRun()] } });
    await open();
    const row = (await screen.findByText("build-box")).closest("li")!;
    expect(within(row).getByText("Test")).toBeInTheDocument();
    expect(within(row).getByText("Failed")).toBeInTheDocument();
    expect(within(row).getByText("Exit code: 1")).toBeInTheDocument();
    const counts = within(row).getByText("40 passed, 2 failed, 1 skipped");
    expect(counts).toHaveClass("bad");
    expect(within(row).getByText(/^Ended \d{2}\/\d{2}\/2026 \d{2}:\d{2}$/)).toBeInTheDocument();
  });

  it("marks a clean test run green, and leaves out what the run does not have", async () => {
    serve({
      "GET /issues/i1/runs": { body: [
        issueRun({ runId: "r1", status: "succeeded", exitCode: 0, testsFailed: 0, testsSkipped: null }),
        issueRun({ runId: "r2", targetHostname: "deploy-box", kind: null, status: "running", exitCode: null, testsPassed: null, testsFailed: null, testsSkipped: null, endedAt: null }),
        issueRun({ runId: "r3", targetHostname: "odd-box", kind: "fuzz", testsPassed: null, testsFailed: null, testsSkipped: null }),
      ] },
    });
    await open();
    const clean = (await screen.findByText("build-box")).closest("li")!;
    expect(within(clean).getByText("40 passed, 0 failed, 0 skipped")).toHaveClass("ok");
    expect(within(clean).getByText("Exit code: 0")).toBeInTheDocument();
    const running = screen.getByText("deploy-box").closest("li")!;
    expect(within(running).getByText("Not finished")).toBeInTheDocument();
    expect(within(running).queryByText(/Exit code/)).not.toBeInTheDocument();
    expect(within(running).queryByText(/passed/)).not.toBeInTheDocument();
    expect(running.querySelectorAll(".badge")).toHaveLength(1);
    const odd = screen.getByText("odd-box").closest("li")!;
    expect(within(odd).getByText("fuzz")).toBeInTheDocument();
  });

  it("says when no run is attached", async () => {
    serve();
    await open();
    expect(await screen.findByText("No runs attached yet.")).toBeInTheDocument();
  });

  it("shows a failed read as a notice", async () => {
    serve({ "GET /issues/i1/runs": { status: 500 } });
    await open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("the test runs card: attaching and removing", () => {
  it("offers only the finished runs not yet attached, test runs first, with no command text", async () => {
    const calls = serve({ "GET /issues/i1/runs": { body: linked() }, "GET /workspaces/w1/runs": { body: workspaceRuns() } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Attach a run" }));
    const select = await screen.findByLabelText("Run to attach");
    const names = within(select).getAllByRole("option").map((o) => o.textContent ?? "");
    expect(names).toHaveLength(4);
    expect(names[0]).toBe("–");
    expect(names[1]).toMatch(/^host-test · Test · Failed · 01\/02\/2026 \d{2}:\d{2}$/);
    expect(names[2]).toMatch(/^host-nokind · – · Timed out · 04\/03\/2026 \d{2}:\d{2}$/);
    expect(names[3]).toMatch(/^host-build · Build · Succeeded · 01\/03\/2026 \d{2}:\d{2}$/);
    expect(select.textContent).not.toMatch(/usr\/bin|list the files/);
    expect(calls.some((c) => c.path === "/workspaces/w1/runs?limit=50")).toBe(true);
    expect(screen.getByRole("button", { name: "Attach" })).toBeDisabled();
  });

  it("attaches the chosen run, closes the picker and reads the list again", async () => {
    const calls = serve({
      "GET /issues/i1/runs": () => ({ body: calls.some((x) => x.method === "POST") ? [issueRun({ runId: "r-old-test", targetHostname: "host-test" })] : [] }),
      "GET /workspaces/w1/runs": { body: workspaceRuns() },
      "POST /issues/i1/runs": { status: 204 },
    });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Attach a run" }));
    await userEvent.selectOptions(await screen.findByLabelText("Run to attach"), screen.getByRole("option", { name: /host-test/ }));
    await userEvent.click(screen.getByRole("button", { name: "Attach" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ runId: "r-old-test" }));
    expect(await within(card()).findByText("host-test")).toBeInTheDocument();
    expect(screen.queryByLabelText("Run to attach")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Attach a run" })).toBeInTheDocument();
  });

  it("says a run that is already attached is attached, and keeps the picker", async () => {
    serve({ "GET /workspaces/w1/runs": { body: workspaceRuns() }, "POST /issues/i1/runs": { status: 400, body: { errors: { runId: ["duplicate_link"] } } } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Attach a run" }));
    await userEvent.selectOptions(await screen.findByLabelText("Run to attach"), screen.getByRole("option", { name: /host-build/ }));
    await userEvent.click(screen.getByRole("button", { name: "Attach" }));
    expect(await screen.findByText("This run is already attached to this issue.")).toBeInTheDocument();
    expect(screen.getByLabelText("Run to attach")).toHaveValue("r-build");
  });

  it.each([
    ["runId", "invalid_run", "Pick a run of this workspace."],
    ["runId", "limit_reached", "The most that fits here has been reached."],
    ["status", "archived", "This is archived. Restore it first."],
  ])("shows the API's refusal %s %s", async (field, code, text) => {
    serve({ "GET /workspaces/w1/runs": { body: workspaceRuns() }, "POST /issues/i1/runs": { status: 400, body: { errors: { [field]: [code] } } } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Attach a run" }));
    await userEvent.selectOptions(await screen.findByLabelText("Run to attach"), screen.getByRole("option", { name: /host-build/ }));
    await userEvent.click(screen.getByRole("button", { name: "Attach" }));
    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it("says when there is nothing to attach, and cancels the picker", async () => {
    serve({ "GET /workspaces/w1/runs": { body: [] } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Attach a run" }));
    expect(await screen.findByText("There are no recent finished runs to attach.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Attach" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByText("There are no recent finished runs to attach.")).not.toBeInTheDocument();
  });

  it("closes the picker and shows a notice when the runs cannot be read", async () => {
    serve({ "GET /workspaces/w1/runs": { status: 500 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Attach a run" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Run to attach")).not.toBeInTheDocument();
  });

  it("removes a run", async () => {
    const calls = serve({ "GET /issues/i1/runs": { body: [issueRun()] }, "DELETE /issues/i1/runs/r1": { status: 204 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: /^Remove: build-box/ }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/issues/i1/runs/r1")).toBe(true));
    await waitFor(() => expect(calls.filter((c) => c.path === "/issues/i1/runs" && c.method === "GET")).toHaveLength(2));
  });

  it("shows a refused removal", async () => {
    serve({ "GET /issues/i1/runs": { body: [issueRun()] }, "DELETE /issues/i1/runs/r1": { status: 403 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: /^Remove: build-box/ }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });
});

describe("the test runs card: rights", () => {
  it("shows a reader the runs but neither the picker nor the remove button", async () => {
    serve({ "GET /issues/i1": { body: issueView({ canEdit: false }) }, "GET /issues/i1/runs": { body: [issueRun()] } });
    await open();
    await screen.findByText("build-box");
    expect(screen.queryByRole("button", { name: "Attach a run" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Remove: / })).not.toBeInTheDocument();
  });

  it("shows no write control on an archived issue", async () => {
    serve({ "GET /issues/i1": { body: issueView({}, { archived: true }) }, "GET /issues/i1/runs": { body: [issueRun()] } });
    await open();
    await screen.findByText("build-box");
    expect(screen.queryByRole("button", { name: "Attach a run" })).not.toBeInTheDocument();
  });
});
