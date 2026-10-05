import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow } from "../api/types";
import { Markdown } from "../lib/markdown";
import { buildChat, toolSummary } from "../lib/transcript";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { SessionPage } from "./SessionPage";
import { render } from "@testing-library/react";

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
