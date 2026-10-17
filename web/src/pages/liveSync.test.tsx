import { renderHook, act as hookAct } from "@testing-library/react";
import { act, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionDetail, SubagentRow, TaskRow } from "../api/types";
import { config } from "../config";
import { useLive } from "../lib/live";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { SessionPage } from "./SessionPage";

describe("a live stream that goes quiet", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal("EventSource", FakeEventSource);
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  const quiet = config.liveStaleMs + config.liveWatchMs;

  it("is opened again when the server's pings stop, and the page is told to re-read once it is back", () => {
    const seen: string[] = [];
    const { unmount } = renderHook(() => useLive("w1", (m) => seen.push(m.event)));
    const first = FakeEventSource.last!;
    hookAct(() => first.emit("ready", {}));
    expect(seen).toEqual([]); // the first connection is not a reconnection

    hookAct(() => vi.advanceTimersByTime(quiet));
    const second = FakeEventSource.last!;
    expect(second).not.toBe(first);
    expect(first.closed).toBe(true);
    hookAct(() => second.emit("ready", {}));
    expect(seen).toEqual(["reconnected"]);
    unmount();
  });

  it("stays on the same stream while pings keep coming", () => {
    const { unmount } = renderHook(() => useLive("w1", () => undefined));
    const first = FakeEventSource.last!;
    hookAct(() => first.emit("ready", {}));
    for (let i = 0; i < 12; i++) {
      hookAct(() => vi.advanceTimersByTime(20_000));
      hookAct(() => first.emit("ping", {}));
    }
    expect(FakeEventSource.last).toBe(first);
    expect(first.closed).toBe(false);
    unmount();
  });

  it("re-reads when the tab is shown again or the network returns, and reopens a stream that went quiet meanwhile", () => {
    const seen: string[] = [];
    const { unmount } = renderHook(() => useLive("w1", (m) => seen.push(m.event)));
    const first = FakeEventSource.last!;
    hookAct(() => first.emit("ready", {}));

    hookAct(() => window.dispatchEvent(new Event("online")));
    expect(seen).toEqual(["resume"]);
    expect(FakeEventSource.last).toBe(first);

    vi.setSystemTime(Date.now() + quiet); // the machine slept: no timer ran
    hookAct(() => document.dispatchEvent(new Event("visibilitychange")));
    expect(seen).toEqual(["resume", "resume"]);
    expect(FakeEventSource.last).not.toBe(first);
    unmount();
  });

  it("does not re-read for a tab that is hidden", () => {
    const seen: string[] = [];
    const { unmount } = renderHook(() => useLive("w1", (m) => seen.push(m.event)));
    const state = vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    hookAct(() => document.dispatchEvent(new Event("visibilitychange")));
    expect(seen).toEqual([]);
    state.mockRestore();
    unmount();
  });
});

describe("the session page after the stream was away", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  const task = (id: string, subject: string, status = "pending"): TaskRow => ({ id, externalId: id, subject, status, updatedAt: "" });
  const agent = (id: string, startedAt: string, endedAt: string | null): SubagentRow => ({ id, agentType: id, description: null, status: endedAt ? "finished" : "running", startedAt, endedAt });
  const detail = (tasks: TaskRow[], subagents: SubagentRow[]): SessionDetail => ({
    session: { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: "2026-10-05T10:00:00Z", lastEventAt: "2026-10-05T10:00:00Z",
      endedAt: null, projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 },
    canCommand: true, tasks, subagents, usage: [],
  });

  it("reads the tasks and subagents again after a reconnection, and lists the subagents with their times, running first", async () => {
    let current = detail([task("1", "First step")], []);
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": () => ({ body: current }),
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [], next: null } },
    });
    renderAt("/w/w1/sessions/s1", [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }]);
    await screen.findByText("First step");
    act(() => FakeEventSource.last!.emit("ready", {}));

    current = detail([task("1", "First step", "completed"), task("2", "Second step")], [
      agent("finished-agent", "2026-10-05T10:00:00Z", "2026-10-05T10:02:19Z"),
      agent("running-agent", "2026-10-05T10:05:00Z", null),
    ]);
    act(() => FakeEventSource.last!.emit("ready", {})); // the stream came back: nothing was said about what happened meanwhile
    await screen.findByText("Second step");

    const card = screen.getByRole("heading", { name: "Subagents" }).closest("section")!;
    const names = [...card.querySelectorAll("li > strong")].map((e) => e.textContent);
    expect(names).toEqual(["running-agent", "finished-agent"]);
    await waitFor(() => expect(within(card).getByText("2m 19s")).toBeInTheDocument()); // the finished one: its length
    expect(within(card).getAllByText("Started")).toHaveLength(2);
    expect(within(card).getAllByText("Ended")).toHaveLength(1); // only the finished one has an end
    expect(within(card).getAllByText("Duration")).toHaveLength(2); // the running one counts up
  });
});
