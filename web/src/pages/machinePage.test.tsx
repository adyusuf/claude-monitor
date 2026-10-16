import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { alert, grant, job, machine, run, sample } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent, count, settle } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("the machine page", () => {
  const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }, { path: "/w/:ws/machines", element: <div>machines list</div> }];
  const older = (i: number): WebRunView => run({ id: `old${i}`, status: "succeeded", canDecide: false, createdAt: new Date(2026, 8, 1, 0, 0, 59 - i).toISOString() });
  const fiftyRuns = Array.from({ length: config.runPageSize }, (_, i) => older(i));

  const api = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [alert({ subject: "" })] },
    "GET /workspaces/w1/runs": { body: [run({ id: "waiting", argv: ["/usr/bin/waiting-cmd"] }), run({ id: "done", status: "succeeded", canDecide: false, argv: ["/usr/bin/done-cmd"] })] },
    "GET /agents/a1/grants": { body: [grant({ granteeName: "Grace" })] },
    "GET /agents/a1/jobs": { body: [job({ name: "nightly-backup" })] },
    "GET /agents/a1/metrics": { body: [sample({ cpuPct: 33 })] },
    ...over,
  });
  const open = () => renderAt("/w/w1/machines/a1", route);

  it("shows the machine, its metrics, alerts, runs, grants and jobs read from the API", async () => {
    const calls = api();
    open();
    expect(await screen.findByRole("heading", { level: 1, name: /laptop-1/ })).toBeInTheDocument();
    expect(screen.getByText("Runs: commands")).toBeInTheDocument();
    expect(await screen.findByRole("img", { name: "CPU 33%" })).toBeInTheDocument();
    expect(screen.getByText("Open")).toBeInTheDocument();
    expect(screen.getByText("/usr/bin/done-cmd")).toBeInTheDocument();
    expect(screen.getByText("Grace")).toBeInTheDocument();
    expect(screen.getByText("nightly-backup")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Machines/ })).toHaveAttribute("href", "/w/w1/machines");
    const paths = calls.map((c) => c.path);
    expect(paths).toContain(`/agents/a1/metrics?minutes=${config.metricsMinutes}`);
    expect(paths).toContain("/workspaces/w1/alerts?agent=a1&resolved=false");
    expect(paths).toContain(`/workspaces/w1/runs?agent=a1&limit=${config.runPageSize}`);
  });

  it("puts only the runs waiting for this user in the approval section, once, and not in the history as approvals", async () => {
    api();
    open();
    const waiting = await screen.findByRole("region", { name: en.remote.waiting });
    expect(within(waiting).getAllByRole("button", { name: "Allow" })).toHaveLength(1);
    expect(within(waiting).getByText("/usr/bin/waiting-cmd")).toBeInTheDocument();
    expect(within(waiting).queryByText("/usr/bin/done-cmd")).toBeNull();
  });

  it("has no approval section when nothing is waiting for the user, even if a run waits for someone else", async () => {
    api({ "GET /workspaces/w1/runs": { body: [run({ canDecide: false })] } });
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("region", { name: en.remote.waiting })).toBeNull();
    expect(screen.queryByRole("button", { name: "Allow" })).toBeNull();
  });

  it("explains an exec level of off, and shows service mode", async () => {
    api({ "GET /workspaces/w1/machines": { body: [machine({ execLevel: "off", serviceMode: true })] } });
    open();
    expect(await screen.findByText(en.remote.execOffNote)).toBeInTheDocument();
    expect(screen.getByText(en.remote.service)).toBeInTheDocument();
  });

  it("shows no off-note for a machine that allows runs", async () => {
    api();
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByText(en.remote.execOffNote)).toBeNull();
    expect(screen.queryByText(en.remote.service)).toBeNull();
  });

  it("offers the new-grant form to the machine's owner and not to anyone else", async () => {
    api();
    const { unmount } = open();
    expect(await screen.findByText(en.remote.grantNew)).toBeInTheDocument();
    unmount();
    api({ "GET /workspaces/w1/agents": { body: [agent({ userId: "u2" })] } });
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByText(en.remote.grantNew)).toBeNull();
  });

  it("says not found for a machine the workspace does not have, with a way back", async () => {
    api({ "GET /workspaces/w1/machines": { body: [machine({ agentId: "other" })] } });
    open();
    expect(await screen.findByText(en.errors.not_found)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Machines/ })).toHaveAttribute("href", "/w/w1/machines");
  });

  it("shows the API's failure when the machine cannot be read", async () => {
    api({ "GET /workspaces/w1/machines": { status: 403 } });
    open();
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
    expect(screen.queryByRole("heading", { level: 1 })).toBeNull();
  });

  it("reads the resolved alerts too when asked", async () => {
    const calls = api();
    open();
    await userEvent.click(await screen.findByRole("checkbox", { name: en.remote.showResolved }));
    await waitFor(() => expect(calls.map((c) => c.path)).toContain("/workspaces/w1/alerts?agent=a1&resolved=true"));
  });

  it("reads everything again after a run, alert, grant or job message, and not after others", async () => {
    const calls = api();
    open();
    await screen.findByRole("heading", { level: 1 });
    await settle(calls);
    const base = count(calls, "/workspaces/w1/runs");
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    for (const ignored of ["session", "command", "permission"]) act(() => FakeEventSource.last!.emit(ignored, {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs * 3));
    expect(count(calls, "/workspaces/w1/runs")).toBe(base);
    for (const [i, name] of ["run", "alert", "grant", "job"].entries()) {
      act(() => FakeEventSource.last!.emit(name, {}));
      await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
      expect(count(calls, "/workspaces/w1/runs")).toBe(base + i + 1);
      expect(count(calls, "/agents/a1/grants")).toBe(base + i + 1);
    }
  });

  it("refreshes the metrics on its own every minute", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    const calls = api();
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(count(calls, "/agents/a1/metrics")).toBe(1);
    await act(() => vi.advanceTimersByTimeAsync(config.metricsRefreshMs - 1));
    expect(count(calls, "/agents/a1/metrics")).toBe(1);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(count(calls, "/agents/a1/metrics")).toBe(2);
    await act(() => vi.advanceTimersByTimeAsync(config.metricsRefreshMs));
    expect(count(calls, "/agents/a1/metrics")).toBe(3);
  });

  it("reads again after a decision is made", async () => {
    const calls = api({ "POST /runs/waiting/deny": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Deny" }));
    await settle(calls);
    const base = count(calls, "/workspaces/w1/runs");
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    await waitFor(() => expect(count(calls, "/workspaces/w1/runs")).toBe(base + 1));
  });

  it("pages older runs with before=the oldest shown, keeps them across a live re-read, and stops offering More", async () => {
    const calls = api({
      "GET /workspaces/w1/runs": (c) => c.path.includes("before=")
        ? { body: [run({ id: "ancient", status: "succeeded", canDecide: false, argv: ["/usr/bin/ancient-cmd"], createdAt: new Date(2026, 7, 1).toISOString() })] }
        : { body: fiftyRuns },
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));
    expect(await screen.findByText("/usr/bin/ancient-cmd")).toBeInTheDocument();
    const paged = calls.find((c) => c.path.includes("before="))!;
    expect(paged.path).toBe(`/workspaces/w1/runs?agent=a1&before=${encodeURIComponent(fiftyRuns[49]!.createdAt)}&limit=${config.runPageSize}`);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
    await settle(calls);
    const reads = calls.filter((c) => c.path.startsWith("/workspaces/w1/runs") && !c.path.includes("before=")).length;
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    act(() => FakeEventSource.last!.emit("run", {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
    expect(calls.filter((c) => c.path.startsWith("/workspaces/w1/runs") && !c.path.includes("before=")).length).toBe(reads + 1);
    expect(screen.getByText("/usr/bin/ancient-cmd")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });

  it("offers More only when a full page came back", async () => {
    api();
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });
});
