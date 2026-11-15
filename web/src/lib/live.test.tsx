import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { FakeEventSource } from "../test/helpers";
import { useLive } from "./live";

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal("EventSource", FakeEventSource);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("useLive handler errors", () => {
  it("lets a handler exception out and calls the handler once, not a second time with a bare event", () => {
    const handler = vi.fn(() => {
      throw new Error("page bug");
    });
    renderHook(() => useLive("w1", handler));
    const stream = FakeEventSource.last!;
    expect(() => stream.emit("session", { sessionId: "s1" })).toThrow("page bug");
    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith({ event: "session", sessionId: "s1" });
  });

  it("passes the bare event when the payload is not JSON", () => {
    const handler = vi.fn();
    renderHook(() => useLive("w1", handler));
    FakeEventSource.last!.emit("permission", "not json");
    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith({ event: "permission" });
  });
});

describe("useLive reconnect back-off", () => {
  const drop = () => {
    const stream = FakeEventSource.last!;
    act(() => stream.onerror!());
    return stream;
  };
  const opened = (before: FakeEventSource) => FakeEventSource.last !== before;

  it("waits longer after every failure, up to a ceiling", () => {
    renderHook(() => useLive("w1", vi.fn()));
    let waits = config.liveReconnectMs;
    for (let i = 0; i < 8; i++) {
      const stream = drop();
      act(() => vi.advanceTimersByTime(waits - 1));
      expect(opened(stream)).toBe(false);
      act(() => vi.advanceTimersByTime(1));
      expect(opened(stream)).toBe(true);
      waits = Math.min(waits * 2, config.liveReconnectMaxMs);
    }
    expect(waits).toBe(config.liveReconnectMaxMs);
  });

  it("goes back to the short wait once the stream is ready again", () => {
    renderHook(() => useLive("w1", vi.fn()));
    for (let i = 0; i < 3; i++) {
      drop();
      act(() => vi.advanceTimersByTime(config.liveReconnectMaxMs));
    }
    act(() => FakeEventSource.last!.emit("ready", {}));
    const stream = drop();
    act(() => vi.advanceTimersByTime(config.liveReconnectMs));
    expect(opened(stream)).toBe(true);
  });
});
