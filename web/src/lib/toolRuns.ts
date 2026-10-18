import type { ChatItem } from "./transcript";

export type ToolItem = Extract<ChatItem, { kind: "tool" }>;
export type ToolState = "pending" | "unknown" | "error" | "done";

/** Where a tool call stands: running, answered (with an error or not), or finished without any output the session kept. */
export const toolState = (item: ToolItem): ToolState => (item.result === null ? (item.settled ? "unknown" : "pending") : item.result.isError ? "error" : "done");

/** Consecutive tool calls shown as one line; its key is the first call's, so the line keeps its place as more calls join it. */
export interface ToolRun { key: string; at: number; tools: ToolItem[] }

export const isRun = <E extends { key: string }>(e: E | ToolRun): e is ToolRun => "tools" in e;

/**
 * Runs of two or more tool calls in a row become one {@link ToolRun}; a lone call, and every other line, stays as it is.
 * Anything between two calls (a message, a question, a command sent from the web) ends the run.
 */
export function groupToolRuns<E extends { key: string; at: number; item?: ChatItem }>(entries: E[]): (E | ToolRun)[] {
  const out: (E | ToolRun)[] = [];
  let run: { entry: E; tool: ToolItem }[] = [];
  const flush = () => {
    if (run.length === 1) out.push(run[0]!.entry);
    else if (run.length > 1) out.push({ key: `run:${run[0]!.entry.key}`, at: run[0]!.entry.at, tools: run.map((r) => r.tool) });
    run = [];
  };
  for (const entry of entries) {
    if (entry.item?.kind === "tool") {
      run.push({ entry, tool: entry.item });
    } else {
      flush();
      out.push(entry);
    }
  }
  flush();
  return out;
}

/** How many calls of each tool, the most used first; tools with the same count keep the order they were first called in. */
export function toolCounts(tools: ToolItem[]): { name: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const tool of tools) counts.set(tool.name, (counts.get(tool.name) ?? 0) + 1);
  return [...counts].map(([name, count]) => ({ name, count })).sort((a, b) => b.count - a.count);
}
