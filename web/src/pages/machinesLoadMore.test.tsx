import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { FakeEventSource, ME, mockApi, renderAt, type Call } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinesPage } from "./MachinesPage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const route = [{ path: "/w/:ws/machines", element: <MachinesPage /> }, { path: "/w/:ws/machines/:agentId", element: <div>machine page</div> }];
const cut = { "X-Truncated": "true" };
const more = () => screen.queryByRole("button", { name: "Load more" });
const reads = (calls: Call[], path: string) => calls.filter((c) => c.method === "GET" && c.path.split("?")[0] === path).map((c) => c.path);
const first = { agents: [agent({ id: "a1", hostname: "box-1" }), agent({ id: "a2", hostname: "box-2" })], machines: [machine({ agentId: "a1", hostname: "box-1" })] };
/** The second page repeats a row already shown (it moved between the reads): it is listed once. */
const second = { agents: [agent({ id: "a2", hostname: "box-2" }), agent({ id: "a3", hostname: "box-3" })], machines: [machine({ agentId: "a3", hostname: "box-3" })] };

/** Agents and machines cut on the first read, whole after the skip of the rows shown; `failing` makes the later pages answer 500. */
const serve = (failing = false) => mockApi({
  "GET /me": { body: ME },
  "GET /workspaces/w1/agents": (call) => (call.path.includes("skip=") ? (failing ? { status: 500 } : { body: second.agents }) : { body: first.agents, headers: cut }),
  "GET /workspaces/w1/machines": (call) => (call.path.includes("skip=") ? (failing ? { status: 500 } : { body: second.machines }) : { body: first.machines, headers: cut }),
});

describe("Load more on the machines the API cut (F-024)", () => {
  it("offers no note and no button under whole lists", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: first.agents }, "GET /workspaces/w1/machines": { body: first.machines } });
    renderAt("/w/w1/machines", route);
    await screen.findByText("box-2");
    expect(screen.queryByText(/Only the first/)).not.toBeInTheDocument();
    expect(more()).not.toBeInTheDocument();
  });

  it("reads the agents and machines after those shown, adds each once, and drops the button on the last page", async () => {
    const calls = serve();
    renderAt("/w/w1/machines", route);
    expect(await screen.findByText("Only the first 2 machines are shown; the workspace has more.")).toBeInTheDocument();
    await userEvent.click(more()!);
    expect(await screen.findByRole("link", { name: "box-3" })).toBeInTheDocument(); // its machine view came with the second page
    expect(screen.getAllByText("box-2")).toHaveLength(1);
    expect(more()).not.toBeInTheDocument();
    expect(screen.queryByText(/Only the first/)).not.toBeInTheDocument();
    expect(reads(calls, "/workspaces/w1/agents")).toEqual(["/workspaces/w1/agents", "/workspaces/w1/agents?skip=2"]);
    expect(reads(calls, "/workspaces/w1/machines")).toEqual(["/workspaces/w1/machines", "/workspaces/w1/machines?skip=1"]);
  });

  it("keeps the rows Load more added when a live message reads the list again", async () => {
    const calls = serve();
    renderAt("/w/w1/machines", route);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    await screen.findByRole("link", { name: "box-3" });
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    act(() => FakeEventSource.last!.emit("machine", {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
    expect(reads(calls, "/workspaces/w1/agents")).toEqual(["/workspaces/w1/agents", "/workspaces/w1/agents?skip=2", "/workspaces/w1/agents", "/workspaces/w1/agents?skip=2"]);
    expect(screen.getByRole("link", { name: "box-3" })).toBeInTheDocument();
    expect(more()).not.toBeInTheDocument();
  });

  it("shows the page's error when the next page fails, keeping the machines shown and the button", async () => {
    serve(true);
    renderAt("/w/w1/machines", route);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByText("box-2")).toBeInTheDocument();
    expect(more()).toBeInTheDocument();
  });
});

/**
 * Holds the next fetch whose path matches until `release` is called; every other call goes through. `held` is true once that call was made.
 * The answer is still the mocked one: it is read when the hold ends.
 */
function holdNext(match: (path: string) => boolean) {
  const spy = vi.mocked(globalThis.fetch);
  const answer = spy.getMockImplementation()!;
  let release = () => {};
  const gate = new Promise<void>((resolve) => (release = resolve));
  const state = { held: false, release: () => release() };
  spy.mockImplementation(async (input, init) => {
    const path = new URL(String(input), "http://test.local").pathname + new URL(String(input), "http://test.local").search;
    if (!state.held && match(path)) {
      state.held = true;
      await gate;
    }
    return answer(input, init);
  });
  return state;
}

describe("Load more and a live refresh that overlap (F-024)", () => {
  const refresh = () => act(() => FakeEventSource.last!.emit("machine", {}));

  it("keeps the rows Load more added when an earlier-started refresh ends after it", async () => {
    serve();
    renderAt("/w/w1/machines", route);
    await screen.findByText("Only the first 2 machines are shown; the workspace has more.");
    const hold = holdNext((path) => path === "/api/workspaces/w1/agents");
    refresh();
    await waitFor(() => expect(hold.held).toBe(true), { timeout: config.liveDebounceMs * 5 });
    await userEvent.click(more()!);
    expect(await screen.findByRole("link", { name: "box-3" })).toBeInTheDocument();
    await act(async () => hold.release());
    await act(async () => {}); // the held refresh has answered with the first page (still cut)
    expect(screen.getByRole("link", { name: "box-3" })).toBeInTheDocument();
    expect(more()).not.toBeInTheDocument();
  });

  it("shows the refresh's rows and the added ones when Load more ends after a refresh that started later", async () => {
    let renamed = false;
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": (call) => (call.path.includes("skip=") ? { body: second.agents }
        : { body: [agent({ id: "a1", hostname: renamed ? "box-1-renamed" : "box-1" }), first.agents[1]], headers: cut }),
      "GET /workspaces/w1/machines": (call) => (call.path.includes("skip=") ? { body: second.machines } : { body: first.machines, headers: cut }),
    });
    renderAt("/w/w1/machines", route);
    await screen.findByText("Only the first 2 machines are shown; the workspace has more.");
    const hold = holdNext((path) => path === "/api/workspaces/w1/agents?skip=2");
    await userEvent.click(more()!);
    await waitFor(() => expect(hold.held).toBe(true));
    renamed = true;
    refresh();
    // The refresh reads as far as Load more asked, so it brings the added rows itself.
    expect(await screen.findByText("box-1-renamed", {}, { timeout: config.liveDebounceMs * 5 })).toBeInTheDocument();
    expect(await screen.findByRole("link", { name: "box-3" })).toBeInTheDocument();
    await act(async () => hold.release());
    await act(async () => {}); // the older Load more has answered: it must not bring back what it read before the refresh
    expect(screen.getByText("box-1-renamed")).toBeInTheDocument();
    expect(screen.queryByText("box-1")).not.toBeInTheDocument();
    expect(screen.getAllByRole("link", { name: "box-3" })).toHaveLength(1);
    expect(more()).not.toBeInTheDocument();
  });
});
