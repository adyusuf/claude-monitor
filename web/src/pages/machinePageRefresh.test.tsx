import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineDetail, ScheduleView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, openTab, renderAt } from "../test/helpers";
import { job, machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent, count, settle } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const schedule = (): ScheduleView => ({
  id: "s1", jobId: "j1", jobName: "Run tests", targetAgentId: "a1", kind: "interval", intervalMinutes: 60, dailyAtMinutes: null, status: "active", pauseReason: null,
  nextRunAt: null, lastRunAt: null, lastRunStatus: null, failuresInARow: 0, ownerUserId: "u1", isOwner: true, createdAt: new Date(2026, 9, 5).toISOString(),
});
const detail = (): MachineDetail => ({ machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [] });

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
    "GET /agents/a1/asks": { body: [] },
    "GET /agents/a1/schedules": { body: [] },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);

/** A live message and the debounce after it: the page reads everything again. */
async function liveReread(event = "run") {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  act(() => FakeEventSource.last!.emit(event, {}));
  await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
  vi.useRealTimers();
}

describe("a live re-read of the machine page that partly fails", () => {
  it("keeps the Control tab and what was typed in it when the detail fails with a server error, and says so", async () => {
    let failing = false;
    const calls = api({
      "GET /agents/a1/detail": () => (failing ? { status: 500, body: { title: "server_error" } } : { body: detail() }),
      "PUT /agents/a1/asks": { status: 403, body: { title: "reauth_required" } },
    });
    open();
    await openTab(en.views.machine.control);
    await userEvent.click(await screen.findByRole("button", { name: en.machines.asks.turnOn }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123");
    await settle(calls);

    failing = true;
    await liveReread();

    expect(screen.getByRole("tab", { name: en.views.machine.control })).toBeInTheDocument();
    expect(screen.getByLabelText(en.remote.codeLabel)).toHaveValue("123");
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
  });

  it("still treats a missing detail endpoint (an older API) as no detail, with no error", async () => {
    api();
    open();
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("tab", { name: en.views.machine.control })).toBeNull();
    expect(screen.queryByText(en.errors.not_found)).toBeNull();
  });

  it("shows an error when the schedules cannot be read, and keeps the ones it had instead of saying there are none", async () => {
    let failing = false;
    const calls = api({
      "GET /agents/a1/detail": { body: detail() },
      "GET /agents/a1/jobs": { body: [job({ name: "Run tests" })] },
      "GET /agents/a1/schedules": () => (failing ? { status: 500, body: { title: "server_error" } } : { body: [schedule()] }),
    });
    open();
    await openTab(en.views.machine.work);
    expect(await screen.findByText(en.schedules.status.active)).toBeInTheDocument();
    await settle(calls);

    failing = true;
    await liveReread();

    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.getByText(en.schedules.status.active)).toBeInTheDocument();
    expect(screen.queryByText(en.schedules.none)).toBeNull();
  });
});

describe("what a live message makes the machine page read again", () => {
  it("reads the schedules again after a schedule message (a pause or end by the server)", async () => {
    const calls = api({ "GET /agents/a1/detail": { body: detail() } });
    open();
    await screen.findByRole("heading", { level: 1 });
    await settle(calls);
    const base = count(calls, "/agents/a1/schedules");

    await liveReread("schedule");

    expect(count(calls, "/agents/a1/schedules")).toBe(base + 1);
  });

  it("reads the owner's asks again on a live re-read, not only when the opt-in changes", async () => {
    const calls = api({ "GET /agents/a1/detail": { body: detail() } });
    open();
    await openTab(en.views.machine.control);
    await screen.findByText(en.machines.asks.none);
    await settle(calls);
    const base = count(calls, "/agents/a1/asks");

    await liveReread("run");

    expect(count(calls, "/agents/a1/asks")).toBe(base + 1);
  });

  it("reads the owner's asks again after an ask message (the server tells only the people who may read the ask)", async () => {
    const calls = api({ "GET /agents/a1/detail": { body: detail() } });
    open();
    await openTab(en.views.machine.control);
    await screen.findByText(en.machines.asks.none);
    await settle(calls);
    const base = count(calls, "/agents/a1/asks");

    await liveReread("ask");

    expect(count(calls, "/agents/a1/asks")).toBe(base + 1);
  });
});
