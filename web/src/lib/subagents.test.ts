import { describe, expect, it } from "vitest";
import type { SubagentRow } from "../api/types";
import { duration } from "./format";
import { isRunning, orderSubagents, runtimeMs } from "./subagents";

const row = (id: string, startedAt: string, endedAt: string | null = null): SubagentRow =>
  ({ id, agentType: id, description: null, status: endedAt ? "finished" : "running", startedAt, endedAt });

describe("ordering subagents", () => {
  it("lists the running ones first, the latest started on top, then the finished ones, the latest finished on top", () => {
    const ordered = orderSubagents([
      row("done-early", "2026-10-05T10:00:00Z", "2026-10-05T10:05:00Z"),
      row("run-old", "2026-10-05T10:01:00Z"),
      row("done-late", "2026-10-05T09:00:00Z", "2026-10-05T10:30:00Z"), // started first, ended last
      row("run-new", "2026-10-05T10:20:00Z"),
    ]);
    expect(ordered.map((r) => r.id)).toEqual(["run-new", "run-old", "done-late", "done-early"]);
  });

  it("keeps every subagent and does not change the list it was given", () => {
    const rows = [row("a", "2026-10-05T10:00:00Z"), row("b", "2026-10-05T11:00:00Z", "2026-10-05T11:01:00Z")];
    expect(orderSubagents(rows)).toHaveLength(2);
    expect(rows.map((r) => r.id)).toEqual(["a", "b"]);
  });

  it("puts a time it cannot read last among its kind instead of failing", () => {
    const ordered = orderSubagents([row("bad", "nope"), row("ok", "2026-10-05T10:00:00Z")]);
    expect(ordered.map((r) => r.id)).toEqual(["ok", "bad"]);
  });

  it("says a subagent without an end is running", () => {
    expect(isRunning(row("a", "2026-10-05T10:00:00Z"))).toBe(true);
    expect(isRunning(row("a", "2026-10-05T10:00:00Z", "2026-10-05T10:01:00Z"))).toBe(false);
  });
});

describe("how long a subagent ran", () => {
  const start = "2026-10-05T10:00:00Z";
  const T = Date.parse(start);

  it("counts to its end when it ended and to now while it runs", () => {
    expect(runtimeMs(row("a", start, "2026-10-05T10:02:19Z"), T + 999_999)).toBe(139_000);
    expect(runtimeMs(row("a", start), T + 65_000)).toBe(65_000);
  });

  it("is never negative and is null when a time cannot be read", () => {
    expect(runtimeMs(row("a", start, "2026-10-05T09:00:00Z"), T)).toBe(0);
    expect(runtimeMs(row("a", "nope"), T)).toBeNull();
  });
});

describe("durations", () => {
  const units = { s: "s", m: "m", h: "h" };

  it("shows seconds, minutes with seconds, and hours with minutes and seconds", () => {
    expect(duration(0, units)).toBe("0s");
    expect(duration(42_900, units)).toBe("42s");
    expect(duration(187_000, units)).toBe("3m 07s");
    expect(duration(3_909_000, units)).toBe("1h 05m 09s");
    expect(duration(-5, units)).toBe("0s");
  });
});
