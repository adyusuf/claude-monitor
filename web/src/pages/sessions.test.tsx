import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow, SessionDetail, SessionRow } from "../api/types";
import { config } from "../config";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { summary } from "../lib/activity";
import { SessionPage } from "./SessionPage";
import { SessionsPage } from "./SessionsPage";

const row = (over: Partial<SessionRow> = {}): SessionRow => ({
  id: "s1", harnessKind: "claude_code", title: "Fix the login", model: "claude-opus-5-5", gitBranch: "main", status: "active",
  startedAt: new Date().toISOString(), lastEventAt: new Date().toISOString(), endedAt: null, projectId: "p", projectName: "widgets",
  agentId: "a", hostname: "laptop-1", ownerId: "u1", ownerName: "Örnek Kişi", costUsd: 1.5, openPermissions: 0, ...over,
});

const detail = (over: Partial<SessionDetail> = {}): SessionDetail => ({
  session: row(), canCommand: true,
  tasks: [{ id: "t1", externalId: "1", subject: "Write the parser", status: "completed", updatedAt: "" },
    { id: "t2", externalId: "2", subject: "Test it", status: "in_progress", updatedAt: "" }],
  subagents: [{ id: "r1", agentType: "analyst", description: "find callers", status: "finished", startedAt: new Date().toISOString(), endedAt: null }],
  usage: [{ model: "claude-opus-5-5", inputTokens: 12000, outputTokens: 800, cacheReadTokens: 0, cacheWriteTokens: 0, costUsd: null }],
  ...over,
});

const sessionsRoute = [{ path: "/w/:ws/sessions", element: <SessionsPage /> }, { path: "/w/:ws/sessions/:id", element: <SessionPage /> }];

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("sessions list", () => {
  it("lists sessions, filters, searches, pages and follows the live stream", async () => {
    let reads = 0;
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": (c) => {
        reads++;
        return c.path.includes("cursor=")
          ? { body: { items: [row({ id: "s3", title: null, costUsd: null, status: "ended" })], next: null } }
          : { body: { items: [row(), row({ id: "s2", title: "Waiting one", status: "waiting", openPermissions: 2 })], next: "c1" } };
      },
    });
    renderAt("/w/w1/sessions", sessionsRoute);
    expect(await screen.findByText("Fix the login")).toBeInTheDocument();
    expect(screen.getByText("2 waiting for an answer")).toBeInTheDocument();
    expect(screen.getAllByText("$1.50")).toHaveLength(2);
    await userEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Untitled session")).toBeInTheDocument();
    expect(screen.getByText("cannot be measured")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Needs you" }));
    await waitFor(() => expect(calls.some((c) => c.path.includes("status=waiting"))).toBe(true));
    await userEvent.type(screen.getByRole("searchbox"), "login");
    await waitFor(() => expect(calls.some((c) => c.path.includes("q=login"))).toBe(true));
    const before = reads;
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    await waitFor(() => expect(reads).toBeGreaterThan(before));
  });

  it("guides an empty workspace to the agent download", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { body: { items: [], next: null } } });
    renderAt("/w/w1/sessions", sessionsRoute);
    expect(await screen.findByText("No sessions yet.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Get the agent" })).toHaveAttribute("href", "/download");
  });

  it("shows a load error", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { status: 500 } });
    renderAt("/w/w1/sessions", sessionsRoute);
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});

describe("one session", () => {
  const events: EventRow[] = [
    { id: 3, kind: "hook:UserPromptSubmit", occurredAt: new Date().toISOString(), truncated: false, payload: { prompt: "Fix the login please" } },
    { id: 2, kind: "hook:PreToolUse", occurredAt: new Date().toISOString(), truncated: true, payload: { tool_name: "Bash", tool_input: { command: "npm test" } } },
  ];

  it("shows tasks, subagents, usage, activity and answers a permission request", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail({ session: row({ status: "waiting", openPermissions: 1 }) }) },
      "GET /sessions/s1/permission-requests": { body: [{ id: "p1", toolName: "Bash", toolInput: { command: "git push" }, status: "open", createdAt: "", expiresAt: new Date().toISOString(), decision: null, reason: null, answeredAt: null }] },
      "POST /permission-requests/p1/answer": { status: 204 },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/events": (c) => ({ body: c.path.includes("before=") ? { items: [{ ...events[1]!, id: 1, kind: "note", payload: { text: "a note" } }], next: null } : { items: events, next: "2" } }),
    });
    renderAt("/w/w1/sessions/s1", sessionsRoute);
    expect(await screen.findByText("Write the parser")).toBeInTheDocument();
    expect(screen.getByText("analyst")).toBeInTheDocument();
    expect(screen.getByText("12k")).toBeInTheDocument();
    expect(await screen.findByText("Fix the login please")).toBeInTheDocument();
    expect(screen.getByText("Bash · npm test")).toBeInTheDocument();
    expect(screen.getByText("shortened")).toBeInTheDocument();
    await userEvent.click(screen.getByText("Bash · npm test"));
    expect(screen.getByText(/"command":"npm test"/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Older activity" }));
    expect(screen.queryByText("a note")).not.toBeInTheDocument(); // the quiet lines fold into one closed group
    await userEvent.click(await screen.findByRole("button", { name: /Tool calls: 1 · Other events: 1/ }));
    expect(await screen.findByText("a note")).toBeInTheDocument();

    const permission = (await screen.findByText("Waiting for permission")).closest("section")!;
    await userEvent.type(within(permission).getByPlaceholderText("Reason (optional)"), "not now");
    await userEvent.click(within(permission).getByRole("button", { name: "Deny" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/permission-requests/p1/answer")?.body).toEqual({ decision: "deny", reason: "not now" }));
  });

  it("sends a prompt, stops, cancels, and shows the history", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail() },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [], next: null } },
      "GET /sessions/s1/commands": { body: [{ id: "c1", kind: "prompt", body: "earlier", status: "queued", createdBy: "u1", createdAt: "", expiresAt: "", deliveredAt: null, appliedAt: null, result: null }] },
      "POST /sessions/s1/commands": { status: 201, body: { id: "c2" } },
      "POST /commands/c1/cancel": { status: 204 },
    });
    renderAt("/w/w1/sessions/s1", sessionsRoute);
    const history = (await screen.findByRole("heading", { name: "Send to this session" })).closest("section")!;
    expect(await within(history).findByText("earlier")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(screen.getByText("Write the message to send.")).toBeInTheDocument();
    await userEvent.type(screen.getByPlaceholderText(/A message for Claude/), "Run the tests");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByText("Sent.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Stop the session" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(calls.some((c) => c.path === "/commands/c1/cancel")).toBe(true));
    const sent = calls.filter((c) => c.path === "/sessions/s1/commands" && c.method === "POST").map((c) => c.body);
    expect(sent).toEqual([{ kind: "prompt", body: "Run the tests" }, { kind: "stop" }]);
  });

  it("says what each waiting command is waiting for, how long is left, and why a lapsed one lapsed", async () => {
    const at = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
    const command = (id: string, body: string, status: string, over: Record<string, unknown> = {}) =>
      ({ id, kind: "prompt", body, status, createdBy: "u1", createdAt: "", expiresAt: at(-1), deliveredAt: null, appliedAt: null, result: null, ...over });
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail() },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [], next: null } },
      "GET /sessions/s1/commands": {
        body: [
          command("c1", "one", "queued", { expiresAt: at(23.5) }),
          command("c2", "two", "delivered", { expiresAt: at(95), deliveredAt: at(-1) }),
          command("c3", "three", "expired", { deliveredAt: at(-30) }),
          command("c4", "four", "expired", { expiresAt: at(7) }),
          command("c5", "five", "applied", { expiresAt: at(10) }),
        ],
      },
    });
    renderAt("/w/w1/sessions/s1", sessionsRoute);
    expect(await screen.findByText("waiting for the agent on the machine to pick it up · expires in 24m")).toBeInTheDocument();
    expect(screen.getByText("reached the agent, waiting for the session's next step · expires in 1h 35m")).toBeInTheDocument();
    const history = screen.getByRole("heading", { name: "Send to this session" }).closest("section")!;
    const rowOf = (body: string) => within(history).getByText(body).closest("li")!;
    expect(within(rowOf("three")).getByText("Expired (the session was idle)")).toBeInTheDocument();
    expect(within(rowOf("four")).getByText("Expired (the agent never picked it up)")).toBeInTheDocument();
    expect(within(rowOf("four")).queryByText(/expires in/)).not.toBeInTheDocument();
    expect(rowOf("five")).not.toHaveTextContent(/expires in|waiting/);
  });

  describe("the idle warning above the prompt box", () => {
    const idle = /This session is idle\. A command is applied when something is typed/;
    const open = async (minutesAgo: number) => {
      mockApi({
        "GET /me": { body: ME },
        "GET /sessions/s1": { body: detail({ session: row({ lastEventAt: new Date(Date.now() - minutesAgo * 60_000).toISOString() }) }) },
        "GET /sessions/s1/permission-requests": { body: [] },
        "GET /sessions/s1/events": { body: { items: [], next: null } },
        "GET /sessions/s1/commands": { body: [] },
      });
      renderAt("/w/w1/sessions/s1", sessionsRoute);
      await screen.findByPlaceholderText(/A message for Claude/);
    };

    it("shows once the last event is older than the idle limit", async () => {
      await open(config.idleSessionMinutes + 1);
      expect(screen.getByText(idle)).toBeInTheDocument();
    });

    it("stays away while the session is recent", async () => {
      await open(config.idleSessionMinutes - 1);
      expect(screen.queryByText(idle)).not.toBeInTheDocument();
    });
  });

  it("tells a viewer that only the owner commands, and an ended session takes nothing", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail({ canCommand: false, tasks: [], subagents: [] }) },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [], next: null } },
      "GET /sessions/s1/commands": { body: [] },
    });
    renderAt("/w/w1/sessions/s1", sessionsRoute);
    expect(await screen.findByText(/Only the person whose machine runs this session/)).toBeInTheDocument();
    expect(screen.getByText("No tasks.")).toBeInTheDocument();
    expect(screen.getByText("No subagents.")).toBeInTheDocument();
    expect(screen.getByText("No activity recorded.")).toBeInTheDocument();
  });

  it("shows a session that cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /sessions/s1": { status: 404 } });
    renderAt("/w/w1/sessions/s1", sessionsRoute);
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
  });

  it("summarises each kind of event", () => {
    const e = (kind: string, payload: Record<string, unknown>) => summary({ id: 1, kind, occurredAt: "", truncated: false, payload });
    expect(e("hook:Notification", { message: "needs you" })).toBe("needs you");
    expect(e("hook:SubagentStart", { agent_type: "qa" })).toBe("qa");
    expect(e("hook:PostToolUse", { tool_name: "Read", tool_input: { file_path: "/a.ts" } })).toBe("Read · /a.ts");
    expect(e("transcript", { type: "assistant", message: { content: [{ type: "text", text: "Done." }] } })).toBe("Done.");
    expect(e("transcript", { type: "user" })).toBe("user");
    expect(e("hook:Stop", {})).toBe("");
  });
});
