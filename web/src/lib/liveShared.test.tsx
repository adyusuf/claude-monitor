import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { FakeEventSource, mockApi } from "../test/helpers";
import { useApprovalQueue } from "./approvalQueue";
import { useLive } from "./live";
import { usePendingApprovals } from "./pending";

// F-098: the frame's badges and the page share ONE EventSource per workspace; the first reader opens it, the last one to leave closes it.
const made: FakeEventSource[] = [];
class CountingEventSource extends FakeEventSource {
  constructor(url: string) {
    super(url);
    made.push(this);
  }
}

beforeEach(() => {
  made.length = 0;
  vi.useFakeTimers();
  vi.stubGlobal("EventSource", CountingEventSource);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const ready = (stream: FakeEventSource) => act(() => stream.emit("ready", {}));

describe("one shared stream per workspace", () => {
  it("opens one EventSource for every reader of the same workspace and hands each message to all of them", () => {
    const a = vi.fn();
    const b = vi.fn();
    renderHook(() => useLive("w1", a));
    renderHook(() => useLive("w1", b));
    expect(made).toHaveLength(1);
    act(() => made[0]!.emit("session", { sessionId: "s1" }));
    expect(a).toHaveBeenCalledWith({ event: "session", sessionId: "s1" });
    expect(b).toHaveBeenCalledWith({ event: "session", sessionId: "s1" });
  });

  it("keeps the stream open while a reader is left and closes it when the last one leaves; the next reader opens a new one", () => {
    const first = renderHook(() => useLive("w1", vi.fn()));
    const second = renderHook(() => useLive("w1", vi.fn()));
    first.unmount();
    expect(made[0]!.closed).toBe(false);
    second.unmount();
    expect(made[0]!.closed).toBe(true);
    renderHook(() => useLive("w1", vi.fn()));
    expect(made).toHaveLength(2);
    expect(made[1]!.closed).toBe(false);
  });

  it("opens a separate stream for another workspace", () => {
    renderHook(() => useLive("w1", vi.fn()));
    renderHook(() => useLive("w2", vi.fn()));
    expect(made.map((s) => s.url)).toHaveLength(2);
    expect(made[0]!.url).not.toBe(made[1]!.url);
  });

  it("tells a reader that joins an already connected stream that it is connected", () => {
    const first = renderHook(() => useLive("w1", vi.fn()));
    ready(made[0]!);
    expect(first.result.current).toBe(true);
    const late = renderHook(() => useLive("w1", vi.fn()));
    expect(late.result.current).toBe(true);
    expect(made).toHaveLength(1);
  });

  it("reconnects once for all readers after a drop, and each hears the catch-up", () => {
    const a = vi.fn();
    const b = vi.fn();
    const ra = renderHook(() => useLive("w1", a));
    const rb = renderHook(() => useLive("w1", b));
    ready(made[0]!);
    act(() => made[0]!.onerror!());
    expect([ra.result.current, rb.result.current]).toEqual([false, false]);
    act(() => vi.advanceTimersByTime(config.liveReconnectMs));
    expect(made).toHaveLength(2);
    ready(made[1]!);
    expect([ra.result.current, rb.result.current]).toEqual([true, true]);
    expect(a).toHaveBeenCalledWith({ event: "reconnected" });
    expect(b).toHaveBeenCalledWith({ event: "reconnected" });
  });

  it("still hands a message to the other readers when one handler throws, and throws that error on", () => {
    const good = vi.fn();
    renderHook(() => useLive("w1", () => {
      throw new Error("page bug");
    }));
    renderHook(() => useLive("w1", good));
    expect(() => made[0]!.emit("run", {})).toThrow("page bug");
    expect(good).toHaveBeenCalledWith({ event: "run" });
  });
});

describe("the frame's badges ride on the shared stream", () => {
  it("uses the page's stream, re-reads on a run or permission message, and does not poll while it is connected", async () => {
    const calls = mockApi({ "GET /workspaces/w1/runs": { body: [] }, "GET /workspaces/w1/permission-requests": { body: [] } });
    renderHook(() => useLive("w1", vi.fn()));
    renderHook(() => usePendingApprovals("w1", "/a"));
    renderHook(() => useApprovalQueue("w1", "/a"));
    expect(made).toHaveLength(1);
    ready(made[0]!);
    await act(async () => {});
    const runs = () => calls.filter((c) => c.path.startsWith("/workspaces/w1/runs")).length;
    const asks = () => calls.filter((c) => c.path.startsWith("/workspaces/w1/permission-requests")).length;
    expect([runs(), asks()]).toEqual([1, 1]);

    await act(async () => vi.advanceTimersByTimeAsync(config.pendingPollMs * 3));
    expect([runs(), asks()]).toEqual([1, 1]);

    await act(async () => made[0]!.emit("run", { id: "r1" }));
    expect([runs(), asks()]).toEqual([2, 1]);
    await act(async () => made[0]!.emit("permission", { id: "p1" }));
    expect([runs(), asks()]).toEqual([2, 2]);
  });

  it("falls back to its poll while the stream is down", async () => {
    const calls = mockApi({ "GET /workspaces/w1/runs": { body: [] } });
    renderHook(() => usePendingApprovals("w1", "/a"));
    ready(made[0]!);
    await act(async () => {});
    act(() => made[0]!.onerror!());
    const runs = () => calls.filter((c) => c.path.startsWith("/workspaces/w1/runs")).length;
    const before = runs();
    await act(async () => vi.advanceTimersByTimeAsync(config.pendingPollMs));
    expect(runs()).toBeGreaterThan(before);
  });
});
