import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { type Call, FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, done } from "../test/runs-fixtures";
import { agent, count } from "./machinesFixture";
import { mergeFirstPage, RunsPage } from "./RunsPage";

const route = [{ path: "/w/:ws/runs", element: <RunsPage /> }];

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/**
 * Holds the first request `isHeld` picks until `release` is called. The answer is made when the request is sent, so what comes later is what
 * the server said then; `arrived` turns true once the page has read the held answer's body.
 */
function hold(isHeld: (url: string) => boolean) {
  const spy = vi.mocked(globalThis.fetch);
  const answer = spy.getMockImplementation()!;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  let taken = false;
  let arrived = false;
  spy.mockImplementation(async (input, init) => {
    const response = await answer(input, init);
    if (taken || !isHeld(String(input))) return response;
    taken = true;
    await gate;
    const text = response.text.bind(response);
    response.text = async () => {
      const body = await text();
      arrived = true;
      return body;
    };
    return response;
  });
  return {
    release: async () => {
      release();
      await waitFor(() => expect(arrived).toBe(true));
      await act(async () => {});
    },
  };
}

const runsReads = (calls: Call[]) => count(calls, "/workspaces/w1/runs");
const emit = (event: string) => act(() => FakeEventSource.last!.emit(event, {}));

describe("the runs page follows the live stream", () => {
  const serve = (current: () => WebRunView[]) => mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/agents": { body: [] },
    "GET /workspaces/w1/runs": () => ({ body: current() }),
  });

  it("reads the first page again after a run event, and shows the new run", async () => {
    let list = [done("r1", 5)];
    const calls = serve(() => list);
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    const before = runsReads(calls);
    list = [done("r2", 1), done("r1", 5)];
    emit("run");
    expect(await screen.findByText("/bin/echo r2")).toBeInTheDocument();
    expect(runsReads(calls)).toBe(before + 1);
  });

  it("does not read again for an event about something else", async () => {
    const calls = serve(() => [done("r1", 5)]);
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    const before = runsReads(calls);
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
    emit("session");
    emit("permission");
    await act(async () => { await vi.advanceTimersByTimeAsync(config.liveDebounceMs + 1); });
    vi.useRealTimers();
    expect(runsReads(calls)).toBe(before);
  });

  it("reads again after the stream reconnects, because events may have been missed", async () => {
    let list = [done("r1", 5)];
    serve(() => list);
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    emit("ready");
    list = [done("r2", 1), done("r1", 5)];
    emit("ready");
    expect(await screen.findByText("/bin/echo r2")).toBeInTheDocument();
  });

  it("drops a run the API no longer lists when the first page is shorter than a page", async () => {
    let list = [done("r1", 5), done("r2", 6)];
    serve(() => list);
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r2");
    list = [done("r1", 5)];
    emit("run");
    await waitFor(() => expect(screen.queryByText("/bin/echo r2")).toBeNull());
    expect(screen.getByText("/bin/echo r1")).toBeInTheDocument();
  });

  it("keeps the older runs loaded with 'Load more' when a live read brings only the first page", async () => {
    const first = Array.from({ length: config.runPageSize }, (_, i) => done(`p${i}`, i + 1));
    let head = first;
    serve(() => head);
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [] },
      "GET /workspaces/w1/runs": (c) => ({ body: c.path.includes("before=") ? [done("older", 300)] : head }),
    });
    renderAt("/w/w1/runs", route);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));
    await screen.findByText("/bin/echo older");
    head = [done("fresh", 0), ...first.slice(0, config.runPageSize - 1)];
    emit("run");
    await screen.findByText("/bin/echo fresh");
    expect(screen.getByText("/bin/echo older")).toBeInTheDocument();
    expect(screen.getByText(`/bin/echo p${config.runPageSize - 1}`)).toBeInTheDocument();
    expect(screen.getAllByRole("row")).toHaveLength(config.runPageSize + 3);
  });
});

describe("a response that arrives after the page moved on", () => {
  it("is not shown when the machine filter was changed meanwhile", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent({ id: "a1", hostname: "laptop-1", userName: "Diğer Kişi" })] },
      "GET /workspaces/w1/runs": (c) => ({ body: c.path.includes("agent=a1") ? [done("mine", 1)] : [done("stale", 1)] }),
    });
    const stale = hold((url) => url.includes("/workspaces/w1/runs") && !url.includes("agent="));
    renderAt("/w/w1/runs", route);
    await waitFor(() => expect(screen.getByRole("option", { name: "laptop-1 · Diğer Kişi" })).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByRole("combobox", { name: en.runsPage.machine }), "a1");
    await screen.findByText("/bin/echo mine");
    await stale.release();
    expect(screen.queryByText("/bin/echo stale")).toBeNull();
    expect(screen.getByText("/bin/echo mine")).toBeInTheDocument();
  });

  it("is not shown when a newer read of the same page was already shown", async () => {
    let list = [done("r1", 5)];
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [] },
      "GET /workspaces/w1/runs": () => ({ body: list }),
    });
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    const settled = runsReads(calls);
    const older = hold((url) => url.includes("/workspaces/w1/runs"));
    list = [done("r1", 5)]; // what the server says when the held read is sent
    emit("run");
    await waitFor(() => expect(runsReads(calls)).toBe(settled + 1));
    list = [done("r2", 1), done("r1", 5)]; // what it says for the next, newer read
    emit("run");
    await screen.findByText("/bin/echo r2");
    await older.release();
    expect(screen.getByText("/bin/echo r2")).toBeInTheDocument();
  });
});

describe("putting a re-read first page in front of the runs already loaded", () => {
  const ids = (list: WebRunView[]) => list.map((r) => r.id);
  const SIZE = 3;

  it("uses an empty page as it is, so a list the API emptied is shown empty", () => {
    expect(mergeFirstPage([], [done("a", 1)], SIZE)).toEqual([]);
  });

  it("uses a page shorter than a page as the whole list, dropping what was loaded before", () => {
    expect(ids(mergeFirstPage([done("a", 1), done("b", 2)], [done("a", 1), done("b", 2), done("c", 3)], SIZE))).toEqual(["a", "b"]);
  });

  it("keeps the older runs already loaded behind a full page, each once and in order", () => {
    const page = [done("n", 0), done("a", 1), done("b", 2)];
    const loaded = [done("a", 1), done("b", 2), done("c", 3), done("d", 4)];
    expect(ids(mergeFirstPage(page, loaded, SIZE))).toEqual(["n", "a", "b", "c", "d"]);
  });

  it("drops a loaded run that is newer than the page's oldest and not in the page, because the API no longer lists it", () => {
    const page = [done("n", 0), done("a", 1), done("c", 3)];
    const loaded = [done("a", 1), done("b", 2), done("c", 3), done("d", 4)];
    expect(ids(mergeFirstPage(page, loaded, SIZE))).toEqual(["n", "a", "c", "d"]);
  });

  it("gives the page's own copy of a run, not the loaded one, when both name it", () => {
    const page = [done("a", 1, { status: "failed" }), done("b", 2), done("c", 3)];
    const merged = mergeFirstPage(page, [done("a", 1, { status: "running" })], SIZE);
    expect(merged.find((r) => r.id === "a")!.status).toBe("failed");
    expect(at(1)).toBe(merged.find((r) => r.id === "a")!.createdAt);
  });
});
