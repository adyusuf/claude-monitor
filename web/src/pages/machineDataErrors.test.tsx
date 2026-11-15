import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, openTab, renderAt } from "../test/helpers";
import { machine, run } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent, count } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const open = () => renderAt("/w/w1/machines/a1", route);

function api(over: Parameters<typeof mockApi>[0] = {}) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    ...over,
  });
}

describe("a metrics read that fails once", () => {
  it("shows the failure, and takes it away again when the next metrics read succeeds", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    let reads = 0;
    api({ "GET /agents/a1/metrics": () => (reads++ === 1 ? { status: 500, body: { title: "server_error" } } : { body: [] }) });
    open();
    await screen.findByRole("heading", { level: 1 });
    await waitFor(() => expect(reads).toBe(1));

    await act(() => vi.advanceTimersByTimeAsync(config.metricsRefreshMs));
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();

    await act(() => vi.advanceTimersByTimeAsync(config.metricsRefreshMs));

    await waitFor(() => expect(screen.queryByText(en.errors.server_error)).toBeNull());
    expect(reads).toBe(3);
  });

  it("keeps a failure of the full read when a later metrics read succeeds", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    const calls = api({ "GET /agents/a1/schedules": { status: 500, body: { title: "server_error" } } });
    open();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    const before = count(calls, "/agents/a1/metrics");

    await act(() => vi.advanceTimersByTimeAsync(config.metricsRefreshMs));

    await waitFor(() => expect(count(calls, "/agents/a1/metrics")).toBe(before + 1));
    expect(screen.getByText(en.errors.server_error)).toBeInTheDocument();
  });
});

describe("older runs that cannot be read once", () => {
  it("shows the failure, and takes it away again when a later page is read", async () => {
    const at = (i: number) => new Date(2026, 9, 5, 9, 59 - i).toISOString();
    const full = Array.from({ length: config.runPageSize }, (_, i) => run({ id: `r${i}`, status: "succeeded", canDecide: false, createdAt: at(i) }));
    let older = 0;
    api({
      "GET /workspaces/w1/runs": (c) => c.path.includes("before=")
        ? (older++ === 0 ? { status: 500, body: { title: "server_error" } } : { body: [] })
        : { body: full },
    });
    open();
    await openTab(en.views.machine.work);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: en.sessions.more }));

    await waitFor(() => expect(screen.queryByText(en.errors.server_error)).toBeNull());
    expect(older).toBe(2);
  });
});

