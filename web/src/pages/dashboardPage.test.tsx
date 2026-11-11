import { act, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DashboardView } from "../api/types";
import { config } from "../config";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine, sample } from "../test/remote-fixtures";
import { DashboardPage } from "./DashboardPage";
import { count, text } from "./machinesFixture";

const route = [{ path: "/w/:ws/dashboard", element: <DashboardPage /> }];

const view = (over: Partial<DashboardView> = {}): DashboardView => ({
  days: 30, projects: [], sprints: [], runs: [], approvals: [], openAlerts: 0, ...over,
});

const serve = (dashboard: DashboardView, machines = [machine()]) =>
  mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/dashboard": { body: dashboard }, "GET /workspaces/w1/machines": { body: machines } });

const tile = (label: string) => screen.getByText(label, { selector: ".stat-label" }).closest(".stat-tile") as HTMLElement;
const card = (title: string) => screen.getByRole("heading", { name: title }).closest(".card") as HTMLElement;
/** An element's children as spaced text: the spans sit side by side with no space in the DOM, and the bars have no text of their own. */
const spaced = (e: Element) => [...e.children].map(text).filter(Boolean).join(" ");
const barRows = (c: HTMLElement) => [...c.querySelectorAll(".bar-row")].map(spaced);

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("the dashboard's figures", () => {
  it("counts the machines and how many are online, from the machine list", async () => {
    serve(view(), [machine({ agentId: "a1", online: true }), machine({ agentId: "a2", hostname: "box-2", online: true }), machine({ agentId: "a3", hostname: "box-3", online: false })]);
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    expect(spaced(tile("Machines"))).toBe("Machines 3 2 of 3 online");
  });

  it("shows the open alerts, the active and archived projects, and the active against the passive sprints", async () => {
    serve(view({
      openAlerts: 4,
      projects: [{ state: "active", count: 5 }, { state: "archived", count: 2 }],
      sprints: [{ state: "active", count: 3 }, { state: "planned", count: 2 }, { state: "closed", count: 6 }],
    }));
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    expect(spaced(tile("Open alerts"))).toBe("Open alerts 4");
    expect(spaced(tile("Projects"))).toBe("Projects 5 2 archived");
    expect(spaced(tile("Sprints"))).toBe("Sprints 3 Active 8 Passive (planned or closed)");
  });

  it("states the period the figures cover in the introduction", async () => {
    serve(view({ days: 14 }));
    renderAt("/w/w1/dashboard", route);
    expect(await screen.findByText("The machines of this workspace and what happened on them in the last 14 days.")).toBeInTheDocument();
  });
});

describe("the dashboard's machine tiles", () => {
  it("shows a machine's resources as percentages, its open alerts badge and a link to the machine", async () => {
    serve(view(), [machine({
      openAlerts: 2,
      latest: sample({ cpuPct: 41.6, memUsedBytes: 1, memTotalBytes: 4, disks: [{ mount: "/", usedBytes: 20, totalBytes: 100 }, { mount: "/d", usedBytes: 93, totalBytes: 100 }] }),
    })]);
    renderAt("/w/w1/dashboard", route);
    const link = await screen.findByRole("link", { name: "laptop-1" });
    const tileEl = link.closest(".dash-machine") as HTMLElement;
    expect(link).toHaveAttribute("href", "/w/w1/machines/a1");
    expect(spaced(tileEl.querySelector(".dash-machine-head")!)).toBe("laptop-1 linux Open alerts: 2");
    expect([...tileEl.querySelectorAll(".dash-meter-row")].map(spaced)).toEqual(["CPU 42%", "Memory 25%", "Disk 93%"]);
  });

  it("says 'not measured' for a machine that has reported no sample, and shows no alerts badge for zero alerts", async () => {
    serve(view(), [machine({ latest: null, openAlerts: 0 })]);
    renderAt("/w/w1/dashboard", route);
    const tileEl = (await screen.findByRole("link", { name: "laptop-1" })).closest(".dash-machine") as HTMLElement;
    expect([...tileEl.querySelectorAll(".dash-meter-row")].map(spaced)).toEqual(["CPU not measured", "Memory not measured", "Disk not measured"]);
    expect(within(tileEl).queryByText(/Open alerts/)).toBeNull();
  });

  it("marks a meter at 90% or more as high and one below as normal, and clamps a value above 100%", async () => {
    serve(view(), [machine({ latest: sample({ cpuPct: 140, memUsedBytes: 90, memTotalBytes: 100, disks: [{ mount: "/", usedBytes: 89, totalBytes: 100 }] }) })]);
    renderAt("/w/w1/dashboard", route);
    const tileEl = (await screen.findByRole("link", { name: "laptop-1" })).closest(".dash-machine") as HTMLElement;
    const rows = [...tileEl.querySelectorAll(".dash-meter-row")];
    expect(rows.map(spaced)).toEqual(["CPU 100%", "Memory 90%", "Disk 89%"]);
    expect(rows.map((r) => r.querySelector(".dash-meter-fill")!.classList.contains("dash-meter-high"))).toEqual([true, true, false]);
  });

  it("lists each reported piece of software once, even when it is both an interpreter and a tool, and says so when none was reported", async () => {
    const tool = (name: string, version: string | null) => ({ name, path: `/usr/bin/${name}`, version, trusted: true });
    serve(view(), [
      machine({ agentId: "a1", hostname: "with-soft", interpreters: [tool("node", "22.1"), tool("git", null)], tools: [tool("git", "2.40")] }),
      machine({ agentId: "a2", hostname: "bare" }),
    ]);
    renderAt("/w/w1/dashboard", route);
    const withSoft = (await screen.findByRole("link", { name: "with-soft" })).closest(".dash-machine") as HTMLElement;
    const bare = screen.getByRole("link", { name: "bare" }).closest(".dash-machine") as HTMLElement;
    expect(within(withSoft).getAllByRole("img").filter((e) => e.classList.contains("soft-mark")).map((e) => e.getAttribute("aria-label"))).toEqual(["node 22.1", "git"]);
    expect(within(bare).getByText("Nothing reported")).toBeInTheDocument();
  });
});

describe("the dashboard's bars", () => {
  it("lists the runs by status with the biggest first, leaves out a status with no runs, and sizes each bar against the largest", async () => {
    serve(view({ runs: [{ state: "failed", count: 2 }, { state: "succeeded", count: 8 }, { state: "running", count: 0 }, { state: "cancelled", count: 4 }] }));
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    const runs = card("Runs by status");
    expect(barRows(runs)).toEqual(["Succeeded 8", "Cancelled 4", "Failed 2"]);
    expect([...runs.querySelectorAll<HTMLElement>(".bar-fill")].map((b) => b.style.width)).toEqual(["100%", "50%", "25%"]);
  });

  it("lists the permission requests in the fixed order of outcomes and gives the allowed share of the decided ones, leaving the waiting ones out of it", async () => {
    serve(view({ approvals: [{ state: "open", count: 9 }, { state: "deny", count: 1 }, { state: "auto_allowed", count: 1 }, { state: "allow", count: 2 }] }));
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    const approvals = card("Permission requests");
    expect(barRows(approvals)).toEqual(["Allowed by a person 2", "Allowed by a standing approval 1", "Denied 1", "Waiting 9"]);
    expect(within(approvals).getByText("75% allowed")).toBeInTheDocument();
  });

  it("counts a request nobody answered in time as decided, so it lowers the allowed share", async () => {
    serve(view({ approvals: [{ state: "allow", count: 1 }, { state: "expired", count: 3 }] }));
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    expect(within(card("Permission requests")).getByText("25% allowed")).toBeInTheDocument();
  });
});

describe("the dashboard with nothing to show", () => {
  it("says there is no machine, no run and no permission request, shows zeroes and no allowed share", async () => {
    serve(view(), []);
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    expect(screen.getByText("No machine has joined this workspace yet.")).toBeInTheDocument();
    expect(screen.getByText("No run in this period.")).toBeInTheDocument();
    expect(screen.getByText("No permission request in this period.")).toBeInTheDocument();
    expect(screen.queryByText(/% allowed/)).toBeNull();
    expect(spaced(tile("Machines"))).toBe("Machines 0 0 of 0 online");
    expect(spaced(tile("Open alerts"))).toBe("Open alerts 0");
  });
});

describe("the dashboard's reading and live refresh", () => {
  it("shows the error text and no figures when the first read fails", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/dashboard": { status: 500, body: {} }, "GET /workspaces/w1/machines": { body: [] } });
    renderAt("/w/w1/dashboard", route);
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.queryByRole("heading", { name: "Dashboard" })).toBeNull();
  });

  it("reads again once after a burst of live machine, run, permission or alert events, and keeps the figures on screen", async () => {
    const calls = serve(view({ openAlerts: 1 }));
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    await act(async () => {});
    const before = count(calls, "/workspaces/w1/dashboard");
    vi.useFakeTimers();
    for (const event of ["machine", "run", "permission", "alert"]) act(() => FakeEventSource.last!.emit(event, {}));
    await act(async () => { await vi.advanceTimersByTimeAsync(config.liveDebounceMs + 1); });
    vi.useRealTimers();
    await waitFor(() => expect(count(calls, "/workspaces/w1/dashboard")).toBe(before + 1));
    expect(spaced(tile("Open alerts"))).toBe("Open alerts 1");
  });

  it("does not read again for an event that is about something else, such as a session", async () => {
    const calls = serve(view());
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    await act(async () => {});
    const before = count(calls, "/workspaces/w1/dashboard");
    vi.useFakeTimers();
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    await act(async () => { await vi.advanceTimersByTimeAsync(config.liveDebounceMs + 1); });
    vi.useRealTimers();
    expect(count(calls, "/workspaces/w1/dashboard")).toBe(before);
  });

  it("keeps the figures and shows the error when a refresh fails", async () => {
    let fail = false;
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/dashboard": () => (fail ? { status: 500, body: {} } : { body: view({ openAlerts: 7 }) }),
      "GET /workspaces/w1/machines": { body: [machine()] },
    });
    renderAt("/w/w1/dashboard", route);
    await screen.findByRole("heading", { name: "Dashboard" });
    fail = true;
    vi.useFakeTimers();
    act(() => FakeEventSource.last!.emit("alert", {}));
    await act(async () => { await vi.advanceTimersByTimeAsync(config.liveDebounceMs + 1); });
    vi.useRealTimers();
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(spaced(tile("Open alerts"))).toBe("Open alerts 7");
  });
});
