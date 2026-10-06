import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentRow, WebRunView } from "../api/types";
import { Layout } from "../components/Layout";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt, type Call } from "../test/helpers";
import { alert, grant, job, machine, run, sample } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { MachinesPage } from "./MachinesPage";

const agent = (over: Partial<AgentRow> = {}): AgentRow => ({
  id: "a1", machineId: "m1", hostname: "laptop-1", os: "linux", arch: "x64", version: "0.4.0", status: "active", userId: "u2", userName: "Diğer Kişi",
  enrolledAt: new Date(2026, 9, 1).toISOString(), lastHeartbeatAt: new Date(2026, 9, 5, 9, 30).toISOString(), revokedAt: null, ...over,
});

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const text = (c: Element) => (c.textContent ?? "").replace(/\s+/g, " ").trim();
const count = (calls: Call[], path: string) => calls.filter((c) => c.path.split("?")[0] === path).length;

describe("the machines list", () => {
  const route = [{ path: "/w/:ws/machines", element: <MachinesPage /> }, { path: "/w/:ws/machines/:agentId", element: <div>machine page</div> }];

  it("shows exec level, service, online state, load, fullest disk and open alerts, and links to the machine", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent()] },
      "GET /workspaces/w1/machines": { body: [machine({
        serviceMode: true, openAlerts: 2,
        latest: sample({ cpuPct: 41.6, memUsedBytes: 1, memTotalBytes: 4, disks: [{ mount: "/", usedBytes: 20, totalBytes: 100 }, { mount: "/d", usedBytes: 93, totalBytes: 100 }] }),
      })] },
    });
    renderAt("/w/w1/machines", route);
    const row = (await screen.findByRole("link", { name: "laptop-1" })).closest("tr")!;
    const cells = within(row).getAllByRole("cell").map(text);
    expect(cells.slice(0, 8)).toEqual(["laptop-1 linux x64", "Diğer Kişi", "Runs: commands Service", "42%", "25%", "93%", "2", "0.4.0"]);
    expect(within(row).getByRole("img", { name: "Online" })).toBeInTheDocument();
    expect(within(row).getByText("2")).toHaveClass("status-failed");
    await userEvent.click(within(row).getByRole("link", { name: "laptop-1" }));
    expect(await screen.findByText("machine page")).toBeInTheDocument();
  });

  it("shows a dash for what was not measured, and a quiet zero for no alerts", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent()] },
      "GET /workspaces/w1/machines": { body: [machine({ latest: null, openAlerts: 0, online: false, execLevel: "shell" })] },
    });
    renderAt("/w/w1/machines", route);
    const row = (await screen.findByRole("link", { name: "laptop-1" })).closest("tr")!;
    const cells = within(row).getAllByRole("cell").map(text);
    expect(cells.slice(2, 7)).toEqual(["Runs: shell", "–", "–", "–", "0"]);
    expect(within(row).getByText("0")).not.toHaveClass("status-failed");
    expect(within(row).getByRole("img", { name: "Offline" })).toBeInTheDocument();
  });

  it("falls back to the plain table when the API has no machines endpoint (404): no link, no remote cells", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [agent()] } });
    renderAt("/w/w1/machines", route);
    const name = await screen.findByText("laptop-1");
    expect(screen.queryByRole("link", { name: "laptop-1" })).toBeNull();
    const cells = within(name.closest("tr")!).getAllByRole("cell").map(text);
    expect(cells.slice(2, 7)).toEqual(["", "", "", "", ""]);
    expect(screen.queryByRole("img", { name: "Online" })).toBeNull();
  });

  it("shows an error, not an empty list, when the machines read fails for another reason", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [agent()] }, "GET /workspaces/w1/machines": { status: 403 } });
    renderAt("/w/w1/machines", route);
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
    expect(screen.queryByText("laptop-1")).toBeNull();
  });

  it("reads again on an alert, run or session message, after a pause, and ignores other messages", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent()] },
      "GET /workspaces/w1/machines": { body: [machine()] },
    });
    renderAt("/w/w1/machines", route);
    await screen.findByRole("link", { name: "laptop-1" });
    expect(count(calls, "/workspaces/w1/machines")).toBe(1);
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    for (const ignored of ["command", "permission", "grant", "job"]) act(() => FakeEventSource.last!.emit(ignored, {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs * 3));
    expect(count(calls, "/workspaces/w1/machines")).toBe(1);
    for (const [i, name] of ["alert", "run", "session"].entries()) {
      act(() => FakeEventSource.last!.emit(name, {}));
      await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
      expect(count(calls, "/workspaces/w1/machines")).toBe(i + 2);
    }
  });
});

/** The page reads once before the signed-in user is known and once after; wait for both so a count is a baseline. */
const settle = async (calls: Call[]) => {
  await waitFor(() => expect(calls.filter((c) => c.path.startsWith("/workspaces/w1/runs") && !c.path.includes("before=")).length).toBeGreaterThanOrEqual(2));
  await act(async () => {});
};

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

describe("the navigation badge of runs waiting for the user", () => {
  const frame = (ws = "w1") => renderAt(`/w/${ws}/x`, [{ path: "/w/:ws/*", element: <Layout /> }]);
  const machinesLink = () => screen.getByRole("link", { name: /Machines/ });

  it("counts only pending runs the user may decide", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/runs": { body: [run({ id: "1" }), run({ id: "2" }), run({ id: "3", canDecide: false }), run({ id: "4", status: "running" })] },
    });
    frame();
    await screen.findByRole("link", { name: /Machines/ });
    await waitFor(() => expect(within(machinesLink()).getByTitle("2 waiting for your approval")).toHaveTextContent("2"));
    expect(calls.find((c) => c.path.startsWith("/workspaces/w1/runs"))!.path).toBe(`/workspaces/w1/runs?limit=${config.runPageSize}`);
  });

  it("shows no badge when nothing waits", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/runs": { body: [run({ canDecide: false })] } });
    frame();
    await screen.findByRole("link", { name: /Machines/ });
    await waitFor(() => expect(calls.some((c) => c.path.startsWith("/workspaces/w1/runs"))).toBe(true));
    expect(within(machinesLink()).queryByText(/^\d+$/)).toBeNull();
  });

  it("reads again when the page changes", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/runs": { body: [] } });
    frame();
    await userEvent.click(await screen.findByRole("link", { name: /Members/ }));
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith("/workspaces/w1/runs"))).toHaveLength(2));
  });

  it("shows none when the read fails", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/runs": { status: 500 } });
    frame();
    await screen.findByRole("link", { name: /Machines/ });
    await waitFor(() => expect(calls.some((c) => c.path.startsWith("/workspaces/w1/runs"))).toBe(true));
    expect(within(machinesLink()).queryByText(/^\d+$/)).toBeNull();
  });

  it("reads the workspace in the address, not the first one", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w2/runs": { body: [run()] } });
    frame("w2");
    await waitFor(() => expect(within(machinesLink()).getByTitle("1 waiting for your approval")).toBeInTheDocument());
    expect(calls.some((c) => c.path.startsWith("/workspaces/w1/runs"))).toBe(false);
  });
});
