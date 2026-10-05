import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow } from "../api/types";
import { Markdown } from "../lib/markdown";
import { buildChat, toolSummary } from "../lib/transcript";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { SessionPage } from "./SessionPage";

const at = "2026-10-05T10:00:00Z";
const ev = (id: number, payload: Record<string, unknown>): EventRow => ({ id, kind: "transcript", occurredAt: at, truncated: false, payload });
const user = (id: number, text: string) => ev(id, { type: "user", message: { role: "user", content: text } });
const said = (id: number, text: string) => ev(id, { type: "assistant", message: { role: "assistant", content: [{ type: "text", text }] } });
const call = (id: number, toolId: string, name: string, input: Record<string, unknown>) =>
  ev(id, { type: "assistant", message: { role: "assistant", content: [{ type: "tool_use", id: toolId, name, input }] } });
const back = (id: number, toolId: string, content: unknown, extra: Record<string, unknown> = {}, isError = false) =>
  ev(id, { type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: toolId, content, is_error: isError }] }, ...extra });

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
    ]);
    expect(items).toMatchObject([
      { kind: "note", tone: "notification", text: "Claude needs your permission to use Bash" },
      { kind: "note", tone: "subagent_start", text: "analyst" },
      { kind: "note", tone: "subagent_stop", text: "analyst" },
      { kind: "user", text: "run the tests\nand lint" },
    ]);
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

const detail = {
  session: { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
    projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 },
  canCommand: true, tasks: [], subagents: [], usage: [],
};
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];

describe("the conversation on the session page", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("reads the newest page, puts the oldest at the top, reads older pages on request and follows new messages", async () => {
    let newer: EventRow[] = [];
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": (c) => {
        if (c.path.includes("before=3")) return { body: { items: [user(2, "older question"), user(1, "oldest question")], next: null } };
        return { body: { items: [...newer, call(5, "t1", "Bash", { command: "npm test" }), back(4, "t1", "42 passed"), said(3, "Done.")], next: "3" } };
      },
    });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("npm test")).toBeInTheDocument();
    expect(calls.find((c) => c.path.startsWith("/sessions/s1/events"))!.path).toContain("kind=transcript");
    const order = () => [...document.querySelectorAll(".chat-row")].map((r) => (r as HTMLElement).dataset.kind);
    expect(order()).toEqual(["assistant", "tool"]);
    await userEvent.click(screen.getByText("npm test"));
    expect(screen.getByText("42 passed")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Older activity" }));
    await waitFor(() => expect(order()).toEqual(["user", "user", "assistant", "tool"]));
    expect(screen.getByText("oldest question")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Older activity" })).toBeNull();

    newer = [said(6, "A new reply")];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    expect(await screen.findByText("A new reply")).toBeInTheDocument();
    expect(order().at(-1)).toBe("assistant");
    expect(screen.getByText("oldest question")).toBeInTheDocument(); // what was read before stays
  });

  it("shows a question with its options and which one was chosen, and a tool that failed", async () => {
    const questions = [{ question: "Which way?", header: "Way", multiSelect: false, options: [{ label: "Left", description: "go left" }, { label: "Right", description: "go right" }] }];
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [
        back(4, "t1", "no such file", {}, true),
        back(3, "q1", "answered", { toolUseResult: { answers: { "Which way?": "Right" } } }),
        call(2, "t1", "Bash", { command: "cat x" }),
        call(1, "q1", "AskUserQuestion", { questions }),
      ], next: null } },
    });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("Which way?")).toBeInTheDocument();
    expect(screen.getByText("go left")).toBeInTheDocument();
    expect(screen.getByText(/✓ Right/)).toBeInTheDocument();
    expect(screen.queryByText(/✓ Left/)).toBeNull();
    expect(screen.getByText("failed")).toBeInTheDocument();
  });

  it("starts empty without failing and can be switched to the raw events and back", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [], next: null } },
    });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("No activity recorded.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Raw events" }));
    expect(await screen.findByText("No activity recorded.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Conversation" }));
    expect(await screen.findByLabelText("Session conversation")).toBeInTheDocument();
  });
});

describe("the conversation: long output, plans, notes, catching up and scrolling", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => {
    vi.unstubAllGlobals();
    delete (HTMLElement.prototype as { clientHeight?: number }).clientHeight;
    delete (HTMLElement.prototype as { scrollHeight?: number }).scrollHeight;
  });
  const base = (events: (c: { path: string }) => unknown, extra: Record<string, unknown> = {}) => mockApi({
    "GET /me": { body: ME },
    "GET /sessions/s1": { body: detail },
    "GET /sessions/s1/commands": { body: [] },
    "GET /sessions/s1/permission-requests": { body: [] },
    "GET /sessions/s1/events": (c: { path: string }) => ({ body: events(c) }),
    ...extra,
  } as never);

  it("cuts a long output until asked, and shows a plan, a thought, a note, a running call and an unanswered one", async () => {
    const long = "line\n".repeat(2000);
    base(() => ({ items: [
      call(9, "run", "Bash", { command: "sleep 99" }),
      ev(8, { type: "assistant", message: { role: "assistant", content: [{ type: "tool_use", id: "p1", name: "ExitPlanMode", input: { plan: "# The plan\n- do it" } }] } }),
      { id: 7, kind: "hook:Notification", occurredAt: at, truncated: false, payload: { message: "Claude is waiting for your input" } },
      ev(6, { type: "assistant", message: { role: "assistant", content: [{ type: "thinking", thinking: "let me think" }] } }),
      back(5, "big", long),
      call(4, "big", "Bash", { command: "yes" }),
      call(3, "gone", "Bash", { command: "old" }),
      said(2, "wrap up"),
    ], next: null }));
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("let me think")).toBeInTheDocument();
    expect(screen.getByText("running")).toBeInTheDocument();
    expect(screen.getByText("no output")).toBeInTheDocument();
    expect(screen.getByText("Claude is waiting for you:", { exact: false })).toBeInTheDocument();
    expect(screen.getByText("Waiting for approval")).toBeInTheDocument();
    expect(screen.getByText("do it")).toBeInTheDocument();
    await userEvent.click(screen.getByText("yes"));
    const pre = () => document.querySelector(".chat-output pre")!.textContent!.length;
    expect(pre()).toBeLessThan(4100);
    await userEvent.click(screen.getByRole("button", { name: "Show all" }));
    expect(pre()).toBe(long.length);
    await userEvent.click(screen.getByRole("button", { name: "Show less" }));
    expect(pre()).toBeLessThan(4100);
  });

  it("reads back until it meets what it has when more arrived than a page, and starts over when it cannot meet it", async () => {
    let mode: "first" | "meets" | "never" = "first";
    base((c) => {
      if (mode === "first") return { items: [said(10, "first reply"), said(9, "earlier")], next: "9" };
      const before = /before=(\d+)/.exec(c.path)?.[1];
      if (mode === "meets") {
        if (!before) return { items: [said(15, "reply 15"), said(14, "reply 14")], next: "14" };
        return { items: [said(13, "reply 13"), said(10, "first reply")], next: "10" };
      }
      const top = before ? Number(before) : 1001;
      return { items: [said(top - 1, `far ${top - 1}`), said(top - 2, `far ${top - 2}`)], next: String(top - 2) };
    });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("first reply")).toBeInTheDocument();

    mode = "meets";
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    expect(await screen.findByText("reply 15")).toBeInTheDocument();
    expect(screen.getByText("reply 13")).toBeInTheDocument();
    expect(screen.getAllByText("first reply")).toHaveLength(1);
    expect(screen.getByText("earlier")).toBeInTheDocument();
    expect([...document.querySelectorAll(".chat-row")].map((r) => r.textContent)).toEqual(["earlier", "first reply", "reply 13", "reply 14", "reply 15"]);

    mode = "never";
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    expect(await screen.findByText("far 1000")).toBeInTheDocument();
    expect(screen.queryByText("earlier")).toBeNull();
    expect(screen.queryByText("reply 15")).toBeNull();
  });

  it("reads older pages by itself while the list does not fill the box, and when scrolled to the top", async () => {
    Object.defineProperty(HTMLElement.prototype, "clientHeight", { configurable: true, get: () => 500 });
    Object.defineProperty(HTMLElement.prototype, "scrollHeight", { configurable: true, get: () => 100 });
    const calls = base((c) => {
      const before = /before=(\d+)/.exec(c.path)?.[1];
      if (!before) return { items: [said(5, "reply 5")], next: "5" };
      return before === "5" ? { items: [said(4, "reply 4")], next: "4" } : { items: [said(3, "reply 3")], next: null };
    });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("reply 3")).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/sessions/s1/events"))).toHaveLength(3);
  });

  it("reads the next older page when the reader scrolls to the top", async () => {
    base((c) => (c.path.includes("before=") ? { items: [said(4, "reply 4")], next: null } : { items: [said(5, "reply 5")], next: "5" }));
    renderAt("/w/w1/sessions/s1", route);
    const box = await screen.findByLabelText("Session conversation");
    fireEvent.scroll(box);
    expect(await screen.findByText("reply 4")).toBeInTheDocument();
  });

  it("shows what waits for permission inside the conversation, where it can be answered", async () => {
    base(() => ({ items: [said(1, "hello")], next: null }), {
      "GET /sessions/s1/permission-requests": { body: [{ id: "p1", toolName: "Bash", toolInput: { command: "git push" }, status: "open", createdAt: "", expiresAt: at, decision: null, reason: null, answeredAt: null }] },
    });
    renderAt("/w/w1/sessions/s1", route);
    const waiting = await screen.findByText("Waiting for permission");
    expect(waiting.closest(".chat-card")).not.toBeNull();
    expect(screen.getByRole("button", { name: "Allow" })).toBeInTheDocument();
  });
});
