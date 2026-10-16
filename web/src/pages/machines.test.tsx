import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Layout } from "../components/Layout";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine, run, sample } from "../test/remote-fixtures";
import { MachinesPage } from "./MachinesPage";
import { agent, count, text } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

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
