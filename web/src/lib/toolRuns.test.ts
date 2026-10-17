import { describe, expect, it } from "vitest";
import { groupToolRuns, isRun, toolCounts, toolState, type ToolItem } from "./toolRuns";
import type { ChatItem } from "./transcript";

const tool = (n: number, name = "Bash", over: Partial<ToolItem> = {}): ToolItem =>
  ({ kind: "tool", key: `t${n}`, at: "2026-10-05T10:00:00Z", id: `i${n}`, name, summary: `s${n}`, input: {}, result: { text: "", isError: false, shortened: false }, settled: false, ...over });
const line = (item: ChatItem, at = 0) => ({ key: item.key, at, item });
const said = (n: number): ChatItem => ({ kind: "assistant", key: `a${n}`, at: "2026-10-05T10:00:00Z", text: "hi" });

describe("grouping tool calls into runs", () => {
  it("joins two or more calls in a row into one run keyed by the first call, and leaves a lone call alone", () => {
    const out = groupToolRuns([line(said(1)), line(tool(1)), line(tool(2)), line(tool(3)), line(said(2)), line(tool(4))]);
    expect(out.map((e) => (isRun(e) ? `run:${e.tools.length}` : e.key))).toEqual(["a1", "run:3", "a2", "t4"]);
    const run = out[1]!;
    expect(isRun(run) && run.key).toBe("run:t1");
  });

  it("keeps the run's place and key as more calls join it", () => {
    const first = groupToolRuns([line(tool(1)), line(tool(2))]);
    const more = groupToolRuns([line(tool(1)), line(tool(2)), line(tool(3))]);
    expect(first[0]!.key).toBe(more[0]!.key);
  });

  it("ends a run at a line that is not a call, such as a command sent from the web", () => {
    const out = groupToolRuns([line(tool(1)), line(tool(2)), { key: "command:c1", at: 1 }, line(tool(3)), line(tool(4))]);
    expect(out.map((e) => (isRun(e) ? e.tools.length : e.key))).toEqual([2, "command:c1", 2]);
  });

  it("returns no lines for no lines", () => {
    expect(groupToolRuns([])).toEqual([]);
  });
});

describe("what a run says about its calls", () => {
  it("counts the calls per tool, the most used first and, on a tie, in the order first called", () => {
    const tools = [tool(1, "Write"), tool(2, "Bash"), tool(3, "Read"), tool(4, "Bash"), tool(5, "Write"), tool(6, "Edit")];
    expect(toolCounts(tools)).toEqual([{ name: "Write", count: 2 }, { name: "Bash", count: 2 }, { name: "Read", count: 1 }, { name: "Edit", count: 1 }]);
  });

  it("tells a running call from a failed, a finished and a settled one without output", () => {
    expect(toolState(tool(1, "Bash", { result: null }))).toBe("pending");
    expect(toolState(tool(1, "Bash", { result: null, settled: true }))).toBe("unknown");
    expect(toolState(tool(1, "Bash", { result: { text: "x", isError: true, shortened: false } }))).toBe("error");
    expect(toolState(tool(1))).toBe("done");
  });
});
