import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { mockApi } from "../test/helpers";
import { run } from "../test/remote-fixtures";
import { countdown, percent, share } from "./format";
import { usePendingApprovals } from "./pending";

describe("countdown", () => {
  const now = Date.UTC(2026, 9, 6, 12, 0, 0);
  const at = (ms: number) => new Date(now + ms).toISOString();

  it("writes minutes and zero-padded seconds, rounding a partial second up", () => {
    expect(countdown(at(247_000), now)).toBe("4:07");
    expect(countdown(at(60_000), now)).toBe("1:00");
    expect(countdown(at(59_000), now)).toBe("0:59");
    expect(countdown(at(1), now)).toBe("0:01");
    expect(countdown(at(4_001), now)).toBe("0:05");
    expect(countdown(at(3_600_000), now)).toBe("60:00");
  });

  it("is null when the moment is now, past, missing or not a date", () => {
    expect(countdown(at(0), now)).toBeNull();
    expect(countdown(at(-1000), now)).toBeNull();
    expect(countdown(null, now)).toBeNull();
    expect(countdown(undefined, now)).toBeNull();
    expect(countdown("", now)).toBeNull();
    expect(countdown("not a date", now)).toBeNull();
  });
});

describe("percent", () => {
  it("rounds to a whole percent", () => {
    expect(percent(0)).toBe("0%");
    expect(percent(42.4)).toBe("42%");
    expect(percent(42.5)).toBe("43%");
    expect(percent(100)).toBe("100%");
  });

  it("shows a dash for an unmeasured or non-finite value", () => {
    expect(percent(null)).toBe("–");
    expect(percent(undefined)).toBe("–");
    expect(percent(Number.NaN)).toBe("–");
    expect(percent(Number.POSITIVE_INFINITY)).toBe("–");
  });
});

describe("share", () => {
  it("is used over total as 0-100, without rounding", () => {
    expect(share(1, 4)).toBe(25);
    expect(share(1, 3)).toBeCloseTo(33.333, 2);
    expect(share(0, 10)).toBe(0);
    expect(share(150, 100)).toBe(150);
  });

  it("is 0 when the total is zero or negative", () => {
    expect(share(5, 0)).toBe(0);
    expect(share(5, -10)).toBe(0);
  });
});

describe("usePendingApprovals", () => {
  afterEach(() => vi.useRealTimers());

  it("counts only the pending runs the user may decide", async () => {
    const calls = mockApi({
      "GET /workspaces/w1/runs": { body: [
        run({ id: "1" }), run({ id: "2" }), run({ id: "3", canDecide: false }), run({ id: "4", status: "running" }),
      ] },
    });
    const { result } = renderHook(() => usePendingApprovals("w1", "/a"));
    await waitFor(() => expect(result.current).toBe(2));
    expect(calls[0]!.path).toBe(`/workspaces/w1/runs?limit=${config.runPageSize}`);
  });

  it("counts none when the read fails, and reads nothing without a workspace", async () => {
    const calls = mockApi({ "GET /workspaces/w1/runs": { status: 500 } });
    const { result } = renderHook(() => usePendingApprovals("w1", "/a"));
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(result.current).toBe(0);
    const idle = renderHook(() => usePendingApprovals(undefined, "/a"));
    expect(idle.result.current).toBe(0);
    expect(calls).toHaveLength(1);
  });

  it("falls back to none after a good read is followed by a failed one", async () => {
    let fail = false;
    mockApi({ "GET /workspaces/w1/runs": () => (fail ? { status: 500 } : { body: [run()] }) });
    const { result, rerender } = renderHook(({ key }) => usePendingApprovals("w1", key), { initialProps: { key: "/a" } });
    await waitFor(() => expect(result.current).toBe(1));
    fail = true;
    rerender({ key: "/b" });
    await waitFor(() => expect(result.current).toBe(0));
  });

  it("reads again when the page changes and on its poll interval, and stops after unmount", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
    const calls = mockApi({ "GET /workspaces/w1/runs": { body: [] } });
    const { rerender, unmount } = renderHook(({ key }) => usePendingApprovals("w1", key), { initialProps: { key: "/a" } });
    expect(calls).toHaveLength(1);
    rerender({ key: "/b" });
    expect(calls).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(config.pendingPollMs - 1);
    expect(calls).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(calls).toHaveLength(3);
    unmount();
    await vi.advanceTimersByTimeAsync(config.pendingPollMs * 2);
    expect(calls).toHaveLength(3);
  });
});
