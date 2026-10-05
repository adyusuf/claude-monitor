import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, back, call, ev, said, user } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

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
