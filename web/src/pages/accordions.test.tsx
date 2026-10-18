import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, back, call, said } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const detail = {
  session: { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
    projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 },
  canCommand: true, tasks: [], subagents: [], usage: [],
};
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];

/** The events as the API sends them: newest first. */
const open = (oldestFirst: EventRow[]) => mockApi({
  "GET /me": { body: ME },
  "GET /sessions/s1": { body: detail },
  "GET /sessions/s1/commands": { body: [] },
  "GET /sessions/s1/permission-requests": { body: [] },
  "GET /sessions/s1/events": { body: { items: [...oldestFirst].reverse(), next: null } },
});

const kinds = () => [...document.querySelectorAll(".chat-row")].map((r) => (r as HTMLElement).dataset.kind);
const LONG = "C:\\Work\\Example\\.claude\\worktrees\\api-build-deployment-check-d5b74c\\scripts\\lib\\npm-deps.ps1";

describe("runs of tool calls in the conversation", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("closes a run to how many calls and of which tools, and opens to every call with its text whole", async () => {
    open([
      said(1, "Starting."),
      call(2, "p1", "mcp__ccd_turn__update_plan", { done: [1] }), back(3, "p1", "ok"),
      call(4, "b1", "Bash", { command: "cd /c/Work/example && ls" }), back(5, "b1", "x"),
      call(6, "b2", "Bash", { command: "git status" }), back(7, "b2", "x"),
      call(8, "b3", "Bash", { command: "git log" }), back(9, "b3", "x"),
      call(10, "w1", "Write", { file_path: LONG }), back(11, "w1", "x"),
    ]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("5 tool calls")).toBeInTheDocument();
    expect(kinds()).toEqual(["assistant", "toolrun"]);
    expect(screen.getByText("Bash ×3")).toBeInTheDocument();
    expect(screen.getByText("Write ×1")).toBeInTheDocument();
    expect(screen.getByText("mcp__ccd_turn__update_plan ×1")).toBeInTheDocument();

    const run = document.querySelector<HTMLDetailsElement>(".chat-tool-run")!;
    expect(run.open).toBe(false);
    await userEvent.click(within(run).getByText("5 tool calls"));
    expect(run.open).toBe(true);
    expect(within(run).getByText(LONG)).toBeInTheDocument(); // the whole path, not cut
    expect(within(run).getByText("cd /c/Work/example && ls")).toBeInTheDocument();
    expect(run.querySelectorAll(".chat-tool-list > .chat-tool")).toHaveLength(5);
  });

  it("says how many of the run failed and is not closed over a running call", async () => {
    open([
      call(1, "a", "Bash", { command: "one" }), back(2, "a", "no such file", {}, true),
      call(3, "b", "Bash", { command: "two" }), back(4, "b", "fine"),
    ]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("1 failed")).toBeInTheDocument();
    expect(document.querySelector(".chat-tool-run")).toHaveClass("chat-tool-error");
  });

  it("marks a run with a call still running as running", async () => {
    open([call(1, "a", "Bash", { command: "one" }), back(2, "a", "ok"), call(3, "b", "Bash", { command: "two" })]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("2 tool calls")).toBeInTheDocument();
    const run = document.querySelector(".chat-tool-run")!;
    expect(run).toHaveClass("chat-tool-pending");
    expect(within(run as HTMLElement).getAllByText("running").length).toBeGreaterThan(0);
  });

  it("splits runs at a message, and a single call is a run of one call", async () => {
    open([
      call(1, "a", "Bash", { command: "one" }), back(2, "a", "ok"),
      call(3, "b", "Read", { file_path: "/x" }), back(4, "b", "ok"),
      said(5, "In between."),
      call(6, "c", "Bash", { command: "three" }), back(7, "c", "ok"),
    ]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("In between.")).toBeInTheDocument();
    expect(kinds()).toEqual(["toolrun", "assistant", "toolrun"]);
    expect(screen.getByText("2 tool calls")).toBeInTheDocument();
    const single = screen.getByText("1 tool call").closest("details")!;
    expect(single.open).toBe(false);
    expect(within(single).getByText("Bash ×1")).toBeInTheDocument();
    await userEvent.click(within(single).getByText("1 tool call"));
    expect(within(single).getByText("three")).toBeVisible();
  });
});

describe("questions in the conversation", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  const questions = [
    { question: "Which way?", header: "Way", multiSelect: false, options: [{ label: "Left", description: "go left" }, { label: "Right", description: "go right" }] },
    { question: "How fast?", header: "Speed", multiSelect: false, options: [{ label: "Slow", description: "" }, { label: "Fast", description: "" }] },
  ];

  it("closes to the questions and what was chosen, and opens to every option", async () => {
    open([
      call(1, "q1", "AskUserQuestion", { questions }),
      back(2, "q1", "answered", { toolUseResult: { answers: { "Which way?": "Right", "How fast?": "Something in between" } } }),
    ]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("Which way?")).toBeInTheDocument();
    const card = document.querySelector<HTMLDetailsElement>(".chat-question")!;
    expect(card.open).toBe(false);
    const summary = card.querySelector("summary")!;
    expect(within(summary).getByText("Which way?")).toBeInTheDocument();
    expect(within(summary).getByText("Answer: Right")).toBeInTheDocument();
    expect(within(summary).getByText("Answer: Something in between")).toBeInTheDocument(); // an answer that is none of the options
    expect(within(summary).queryByText("go left")).toBeNull(); // the options are not in the closed line
    expect(card.querySelectorAll("summary li")).toHaveLength(0);

    await userEvent.click(summary);
    expect(card.open).toBe(true);
    expect(screen.getByText("go left")).toBeVisible();
    expect(screen.getByText("go right")).toBeVisible();
    expect(screen.getByText(/✓ Right/)).toBeInTheDocument();
    expect(screen.queryByText(/✓ Left/)).toBeNull();
    expect(card.querySelectorAll(".chat-options")).toHaveLength(2);
  });

  it("says in the closed line that a question is still waiting for its answer", async () => {
    open([call(1, "q1", "AskUserQuestion", { questions: [questions[0]] })]);
    renderAt("/w/w1/sessions/s1", route);
    const card = (await screen.findByText("Which way?")).closest("details")!;
    expect(within(card.querySelector("summary")!).getByText("Waiting for an answer")).toBeInTheDocument();
    expect(card.open).toBe(false);
  });
});
