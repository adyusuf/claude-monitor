import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { buildChat } from "../lib/transcript";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, back, call, said, user } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const TASK = `<task-notification>
<task-id>b1e4gny2p</task-id>
<output-file>C:\\Temp\\claude\\tasks\\b1e4gny2p.output</output-file>
<status>completed</status>
<summary>Background command "Run admin lint, typecheck and full test suite" completed (exit code 0)</summary>
</task-notification>`;

const detail = {
  session: { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
    projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 },
  canCommand: true, tasks: [], subagents: [], usage: [],
};

describe("what the harness wrote into the session", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("is one closed line with its summary, and opens to plain fields instead of tags", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [user(2, TASK), said(1, "Started it.")], next: null } },
    });
    renderAt("/w/w1/sessions/s1", [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }]);
    const [summary] = await screen.findAllByText(/Run admin lint, typecheck and full test suite/); // the closed line; the field of the same name is inside
    const card = summary!.closest("details")!;
    expect(card.open).toBe(false);
    expect(card.closest("li")).toHaveAttribute("data-kind", "notice");
    expect(within(card.querySelector("summary")!).getByText("Task notification")).toBeInTheDocument();
    expect(within(card.querySelector("summary")!).getByText("completed")).toBeInTheDocument();
    expect(card.querySelector("summary")!.textContent).not.toContain("task-id"); // the rest is inside

    await userEvent.click(card.querySelector("summary")!);
    expect(card.open).toBe(true);
    expect(within(card).getByText("Task id")).toBeVisible();
    expect(within(card).getByText("b1e4gny2p")).toBeVisible();
    expect(within(card).getByText("C:\\Temp\\claude\\tasks\\b1e4gny2p.output")).toBeVisible();
    expect(document.body.textContent).not.toContain("<task-notification>");
    expect(document.body.textContent).not.toContain("</summary>");
  });
});

describe("a notice in the chat items", () => {
  const events = (...e: ReturnType<typeof user>[]) => e;

  it("is not the user speaking: it does not close a next-step question and it does not end a running call", () => {
    const items = buildChat(events(
      call(1, "t1", "Bash", { command: "sleep 99" }),
      user(2, TASK),
    ));
    expect(items.map((i) => i.kind)).toEqual(["tool", "notice"]);
    expect(items[0]).toMatchObject({ kind: "tool", settled: false });
  });

  it("a typed line still is the user speaking", () => {
    const items = buildChat(events(call(1, "t1", "Bash", { command: "x" }), back(2, "t1", "ok"), user(3, "go on")));
    expect(items.map((i) => i.kind)).toEqual(["tool", "user"]);
  });
});
