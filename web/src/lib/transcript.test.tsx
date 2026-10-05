import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { EventRow } from "../api/types";
import { at, back, call, ev, said, user } from "../test/transcript-fixtures";
import { Markdown } from "./markdown";
import { buildChat, toolSummary } from "./transcript";

describe("buildChat", () => {
  it("shows what was asked, what Claude said and what it ran with the result beside it", () => {
    const items = buildChat([
      user(1, "run the tests"),
      said(2, "On it."),
      call(3, "t1", "Bash", { command: "npm test", description: "tests" }),
      back(4, "t1", [{ type: "text", text: "all green" }]),
      call(5, "t2", "Bash", { command: "false" }),
      back(6, "t2", "boom", {}, true),
      call(7, "t3", "Read", { file_path: "/a/b.ts" }),
    ]);
    expect(items.map((i) => i.kind)).toEqual(["user", "assistant", "tool", "tool", "tool"]);
    expect(items[2]).toMatchObject({ name: "Bash", summary: "npm test", result: { text: "all green", isError: false } });
    expect(items[3]).toMatchObject({ result: { text: "boom", isError: true } });
    expect(items[4]).toMatchObject({ summary: "/a/b.ts", result: null });
  });

  it("leaves out reminders, side chains, meta lines, command output and a result whose call is not loaded", () => {
    const items = buildChat([
      back(1, "older-call", "orphan"),
      user(2, "<system-reminder>x</system-reminder>"),
      user(3, "<local-command-stdout>ok</local-command-stdout>"),
      ev(4, { type: "user", isMeta: true, message: { role: "user", content: "meta" } }),
      ev(5, { type: "assistant", isSidechain: true, message: { role: "assistant", content: [{ type: "text", text: "sub" }] } }),
      ev(6, { type: "summary", summary: "x" }),
      user(7, "<command-name>/review</command-name><command-args>the diff</command-args>"),
      ev(8, { type: "assistant", message: { role: "assistant", content: [{ type: "thinking", thinking: "hmm" }, { type: "text", text: "  " }] } }),
    ]);
    expect(items).toMatchObject([{ kind: "user", command: "/review", text: "the diff" }, { kind: "thinking", text: "hmm" }]);
  });

  it("reads a question with its options, the answer given, and a plan", () => {
    const questions = [{ question: "Which way?", header: "Way", multiSelect: false, options: [{ label: "A", description: "first" }, { label: "B", description: "" }] }];
    const items = buildChat([
      call(1, "q1", "AskUserQuestion", { questions }),
      back(2, "q1", "answered", { toolUseResult: { questions, answers: { "Which way?": "A" } } }),
      call(3, "q2", "AskUserQuestion", { questions }),
      call(4, "p1", "ExitPlanMode", { plan: "# Plan\n- step" }),
    ]);
    expect(items[0]).toMatchObject({ kind: "question", answers: { "Which way?": "A" }, questions: [{ header: "Way", options: [{ label: "A" }, { label: "B" }] }] });
    expect(items[1]).toMatchObject({ kind: "question", answers: null, result: null });
    expect(items[2]).toMatchObject({ kind: "plan", plan: "# Plan\n- step", result: null });
  });

  it("shows a shortened event as a note and summarises each tool by its own field", () => {
    expect(buildChat([{ ...ev(1, { truncated: true, preview: "{\"type\":\"assistant\"" }), truncated: true }])).toMatchObject([{ kind: "shortened" }]);
    expect(toolSummary("Grep", { pattern: "foo" })).toBe("foo");
    expect(toolSummary("WebFetch", { url: "u" })).toBe("u");
    expect(toolSummary("WebSearch", { query: "q" })).toBe("q");
    expect(toolSummary("Agent", { description: "d" })).toBe("d");
    expect(toolSummary("TodoWrite", { todos: [1, 2] })).toBe("2");
    expect(toolSummary("Custom", { prompt: "p" })).toBe("p");
    expect(toolSummary("Custom", {})).toBe("");
  });
});

describe("buildChat on large, hook and web-sent lines", () => {
  const cut = (id: number, preview: string): EventRow => ({ ...ev(id, { truncated: true, bytes: 99999, preview }), truncated: true });

  it("settles a call whose output was cut for size, by the call id the preview still names", () => {
    const items = buildChat([
      call(1, "t1", "Bash", { command: "big" }),
      cut(2, '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"xxxx'),
      said(3, "after"),
    ]);
    expect(items.map((i) => i.kind)).toEqual(["tool", "assistant"]);
    expect(items[0]).toMatchObject({ result: { shortened: true, isError: false } });
  });

  it("calls a call settled once anything was said after it, and running only while it is the last thing", () => {
    const items = buildChat([
      call(1, "lost", "Bash", { command: "a" }),
      cut(2, '{"type":"user","message":{"content":[{"type":"tool_result","content":"no id in the preview'),
      call(3, "late", "Bash", { command: "b" }),
      call(4, "late2", "Read", { file_path: "/x" }),
    ]);
    expect(items.map((i) => i.kind)).toEqual(["tool", "shortened", "tool", "tool"]);
    expect(items.filter((i) => i.kind === "tool").map((i) => i.kind === "tool" && i.settled)).toEqual([true, false, false]);
  });

  it("shows a notification and the subagents coming and going as notes, and a web message as the user's", () => {
    const hook = (id: number, kind: string, payload: Record<string, unknown>): EventRow => ({ id, kind, occurredAt: at, truncated: false, payload });
    const items = buildChat([
      hook(1, "hook:Notification", { message: "Claude needs your permission to use Bash" }),
      hook(2, "hook:SubagentStart", { agent_type: "analyst" }),
      hook(3, "hook:SubagentStop", { agent_type: "analyst" }),
      ev(4, { type: "attachment", attachment: { type: "hook_additional_context", content: ["Messages sent to this session from Claude Monitor (the web):\n- run the tests\n- and lint"] } }),
      ev(5, { type: "attachment", attachment: { type: "hook_additional_context", content: "something else" } }),
      ev(6, { type: "attachment", attachment: { type: "date" } }),
      ev(7, { type: "attachment", attachment: { type: "hook_additional_context", content: ["Messages sent to this session from Claude Monitor (the web):"] } }),
      ev(8, { type: "user", isMeta: true, message: { role: "user", content: "Stop hook feedback:\nrun the harmless command" } }),
      ev(9, { type: "user", isMeta: true, message: { role: "user", content: "Stop hook feedback:\n" } }),
      ev(10, { type: "user", isMeta: true, message: { role: "user", content: "another meta line" } }),
    ]);
    expect(items).toMatchObject([
      { kind: "note", tone: "notification", text: "Claude needs your permission to use Bash" },
      { kind: "note", tone: "subagent_start", text: "analyst" },
      { kind: "note", tone: "subagent_stop", text: "analyst" },
      { kind: "user", text: "run the tests\nand lint" },
      { kind: "user", text: "run the harmless command" },
    ]);
    expect(items).toHaveLength(5);
  });
});

describe("a call that waits for the user", () => {
  it("stays running while only a notification or a subagent's start came after it", () => {
    const note = (id: number, kind: string): EventRow => ({ id, kind, occurredAt: at, truncated: false, payload: { message: "m", agent_type: "a" } });
    const items = buildChat([call(1, "t", "Bash", { command: "x" }), note(2, "hook:Notification"), note(3, "hook:SubagentStart")]);
    expect(items[0]).toMatchObject({ kind: "tool", settled: false });
  });
});

describe("Markdown", () => {
  it("renders code, lists, headings and emphasis without building HTML from the text", () => {
    const { container } = render(<Markdown text={"# Title\nSome `code` and **bold** <b>x</b>\n\n- one\n- two\n\n1. a\n2) b\n\n```sh\nnpm test\n```\n```\nunfinished"} />);
    expect(container.querySelector("strong")?.textContent).toBe("Title");
    expect(container.querySelector("p code")?.textContent).toBe("code");
    expect(container.querySelectorAll("ul li")).toHaveLength(2);
    expect(container.querySelectorAll("ol li")).toHaveLength(2);
    expect(container.querySelectorAll("pre")).toHaveLength(2);
    expect(container.textContent).toContain("<b>x</b>");
    expect(container.querySelector("b")).toBeNull();
  });
});

