import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { done } from "../test/runs-fixtures";
import { count } from "./machinesFixture";
import { RunsPage } from "./RunsPage";

const route = [{ path: "/w/:ws/runs", element: <RunsPage /> }];

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const serve = (list: WebRunView[], extra: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /workspaces/w1/agents": { body: [] },
  "GET /workspaces/w1/runs": { body: list },
  ...extra,
});
const open = async (id: string) => {
  renderAt("/w/w1/runs", route);
  const row = (await screen.findByText(`/bin/echo ${id}`)).closest("tr") as HTMLElement;
  await userEvent.click(within(row).getByRole("button", { name: en.runsPage.details }));
  return screen.findByRole("dialog");
};

describe("a run in the popup", () => {
  it("opens the run's details under its status, with when it was made, who decided it and its facts, and closes again", async () => {
    serve([done("r1", 1, { decidedByName: "Örnek Kişi", grantId: null })], { "GET /runs/r1": { body: done("r1", 1, { decidedByName: "Örnek Kişi" }) } });
    const dialog = await open("r1");
    expect(within(dialog).getByRole("heading", { name: `${en.runsPage.detailsOf} ${en.remote.status.succeeded}` })).toBeInTheDocument();
    expect(within(dialog).getByText(en.runsPage.decidedBy).nextElementSibling).toHaveTextContent("Örnek Kişi");
    expect(within(dialog).getByText(en.runsPage.when).nextElementSibling).toHaveTextContent("05/10/2026 11:59");
    expect(within(dialog).getByText(en.remote.target)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: en.common.close }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("reads the run again when it opens, so the popup shows its latest status, not the one in the list", async () => {
    const calls = serve([done("r1", 1, { status: "running" })], { "GET /runs/r1": { body: done("r1", 1, { status: "failed", exitCode: 2 }) } });
    const dialog = await open("r1");
    expect(await within(dialog).findByRole("heading", { name: `${en.runsPage.detailsOf} ${en.remote.status.failed}` })).toBeInTheDocument();
    expect(calls.some((c) => c.method === "GET" && c.path === "/runs/r1")).toBe(true);
    expect(within(dialog).getByText(en.remote.exitCode.replace("{code}", "2"))).toBeInTheDocument();
  });

  it("keeps the run as the list had it when the closer look cannot be read", async () => {
    serve([done("r1", 1, { status: "running" })], { "GET /runs/r1": { status: 500, body: {} } });
    const dialog = await open("r1");
    expect(within(dialog).getByRole("heading", { name: `${en.runsPage.detailsOf} ${en.remote.status.running}` })).toBeInTheDocument();
    expect(within(dialog).queryByRole("alert")).toBeNull();
  });

  it("shows the approval card, not the plain facts, for a run that waits for this user's decision", async () => {
    serve([done("r1", 1, { status: "pending_approval", canDecide: true, hash: "h".repeat(64) })], { "GET /runs/r1": { body: done("r1", 1, { status: "pending_approval", canDecide: true, hash: "h".repeat(64) }) } });
    const dialog = await open("r1");
    expect(within(dialog).getByText("Run on build-box")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: en.remote.allow })).toBeInTheDocument();
    expect(within(dialog).queryByText(en.runsPage.decidedBy)).toBeNull();
  });

  it("shows the plain facts for a run that waits for someone else's decision", async () => {
    serve([done("r1", 1, { status: "pending_approval", canDecide: false })], { "GET /runs/r1": { body: done("r1", 1, { status: "pending_approval", canDecide: false }) } });
    const dialog = await open("r1");
    expect(within(dialog).queryByRole("button", { name: en.remote.allow })).toBeNull();
    expect(within(dialog).getByText(en.runsPage.decidedBy).nextElementSibling).toHaveTextContent(en.runsPage.undecided);
  });

  it("sends the approval with the run's hash from the popup and reads the list again", async () => {
    // Allow stays disabled once the wait is over, so the run must still be open whatever the clock says.
    const pending = done("r1", 1, { status: "pending_approval", canDecide: true, hash: "h".repeat(64), expiresAt: "2099-01-01T00:00:00.000Z" });
    const calls = serve([pending], { "GET /runs/r1": { body: pending }, "POST /runs/r1/approve": { status: 204 } });
    const dialog = await open("r1");
    const before = count(calls, "/workspaces/w1/runs");
    await userEvent.click(within(dialog).getByRole("button", { name: en.remote.allow }));
    await waitFor(() => expect(count(calls, "/workspaces/w1/runs")).toBeGreaterThan(before));
    expect(calls.find((c) => c.path === "/runs/r1/approve")!.body).toEqual({ hash: "h".repeat(64) });
  });

  it("sends the denial with the typed reason from the popup", async () => {
    const pending = done("r1", 1, { status: "pending_approval", canDecide: true, hash: "h".repeat(64) });
    const calls = serve([pending], { "GET /runs/r1": { body: pending }, "POST /runs/r1/deny": { status: 204 } });
    const dialog = await open("r1");
    await userEvent.click(within(dialog).getByRole("button", { name: en.remote.deny }));
    await userEvent.type(within(dialog).getByLabelText(en.remote.denyReason), "not now");
    await userEvent.click(within(dialog).getByRole("button", { name: en.remote.deny }));
    await waitFor(() => expect(calls.some((c) => c.path === "/runs/r1/deny")).toBe(true));
    expect(calls.find((c) => c.path === "/runs/r1/deny")!.body).toEqual({ reason: "not now" });
  });

  it("cancels an open run from the popup and reads the list again", async () => {
    const running = done("r1", 1, { status: "running" });
    const calls = serve([running], { "GET /runs/r1": { body: running }, "POST /runs/r1/cancel": { status: 204 } });
    const dialog = await open("r1");
    const before = count(calls, "/workspaces/w1/runs");
    await userEvent.click(within(dialog).getByRole("button", { name: en.remote.cancelRun }));
    await waitFor(() => expect(count(calls, "/workspaces/w1/runs")).toBeGreaterThan(before));
    expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual(["/runs/r1/cancel"]);
  });

  it("shows the API's error in the popup when the cancel fails", async () => {
    const running = done("r1", 1, { status: "running" });
    serve([running], { "GET /runs/r1": { body: running }, "POST /runs/r1/cancel": { status: 409, body: {} } });
    const dialog = await open("r1");
    await userEvent.click(within(dialog).getByRole("button", { name: en.remote.cancelRun }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("That has already changed. Refresh and try again.");
  });

  it("offers no cancel for a finished run", async () => {
    serve([done("r1", 1)], { "GET /runs/r1": { body: done("r1", 1) } });
    const finished = await open("r1");
    expect(within(finished).queryByRole("button", { name: en.remote.cancelRun })).toBeNull();
  });

  it("offers no cancel for a run whose command the user may not see", async () => {
    const hidden = done("r1", 1, { status: "running", visible: false, argv: null });
    serve([hidden], { "GET /runs/r1": { body: hidden } });
    renderAt("/w/w1/runs", route);
    await userEvent.click(await screen.findByRole("button", { name: en.runsPage.details }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByRole("button", { name: en.remote.cancelRun })).toBeNull();
  });
});
