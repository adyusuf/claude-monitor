import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CommandRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, said, user } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const session = { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
  projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 };
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];
const SENT = "2026-10-05T10:05:00Z"; // after the events below (10:00:00Z)
const command = (over: Partial<CommandRow> = {}): CommandRow => ({
  id: "c1", kind: "prompt", body: "please run the tests", status: "queued", createdBy: "u1", createdAt: SENT, expiresAt: "2026-10-05T10:35:00Z",
  deliveredAt: null, appliedAt: null, result: null, ...over,
});

function setup(commands: CommandRow[], canCommand = true) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /sessions/s1": { body: { session, canCommand, tasks: [], subagents: [], usage: [] } },
    "GET /sessions/s1/permission-requests": { body: [] },
    "GET /sessions/s1/events": { body: { items: [said(2, "Done."), user(1, "go")], next: null } },
    "GET /sessions/s1/commands": () => ({ body: commands }),
    "POST /commands/c1/cancel": { status: 204 },
  });
}
const lines = () => [...document.querySelectorAll(".chat-row")].map((r) => (r as HTMLElement).dataset.kind);

describe("what happens to a message sent from the web, in the conversation", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("draws a waiting message after the last line with its status, the reason it waits, and lets it be cancelled there", async () => {
    const calls = setup([command()]);
    renderAt("/w/w1/sessions/s1", route);

    const bubble = (await screen.findByText("please run the tests", { selector: ".chat p" })).closest("li") as HTMLElement;
    expect(lines()).toEqual(["user", "assistant", "command"]);
    expect(within(bubble).getByText("Queued")).toBeInTheDocument();
    expect(within(bubble).getByText(/waiting for the agent on the machine to pick it up/)).toBeInTheDocument();

    await userEvent.click(within(bubble).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/commands/c1/cancel")).toBe(true));
  });

  it("says how a pushed message is going and why a lapsed one lapsed, on the message itself", async () => {
    setup([
      command({ id: "c1", body: "pushed one", status: "delivered", deliveredAt: SENT, transport: "inbox" }),
      command({ id: "c2", body: "lapsed one", status: "expired", createdAt: "2026-10-05T10:06:00Z", deliveredAt: null }),
    ]);
    renderAt("/w/w1/sessions/s1", route);

    const pushed = (await screen.findByText("pushed one", { selector: ".chat p" })).closest("li") as HTMLElement;
    expect(within(pushed).getByText("Delivered")).toBeInTheDocument();
    expect(within(pushed).getByText(/Pushed into the session through the session's inbox/)).toBeInTheDocument();
    const lapsed = screen.getByText("lapsed one", { selector: ".chat p" }).closest("li") as HTMLElement;
    expect(within(lapsed).getByText("Expired")).toBeInTheDocument();
    expect(within(lapsed).getByText(/the agent never picked it up/)).toBeInTheDocument();
    expect(within(lapsed).queryByRole("button", { name: "Cancel" })).toBeNull(); // nothing left to cancel
  });

  it("does not draw an applied message (the session's own line replaces it), a message from before the page's first line, or a cancel button for a viewer", async () => {
    setup([
      command({ id: "c1", body: "already applied", status: "applied", appliedAt: SENT }),
      command({ id: "c2", body: "from last week", createdAt: "2026-09-28T10:00:00Z" }),
      command({ id: "c3", body: "still waiting" }),
    ], false);
    renderAt("/w/w1/sessions/s1", route);

    const waiting = (await screen.findByText("still waiting", { selector: ".chat p" })).closest("li") as HTMLElement;
    expect(lines()).toEqual(["user", "assistant", "command"]);
    expect(screen.queryByText("already applied", { selector: ".chat p" })).toBeNull();
    expect(screen.queryByText("from last week", { selector: ".chat p" })).toBeNull();
    expect(within(waiting).queryByRole("button", { name: "Cancel" })).toBeNull();
  });

  it("shows a stop request as a stop, and a message sent between two lines sits between them", async () => {
    setup([
      command({ id: "c1", kind: "stop", body: null, createdAt: "2026-10-05T10:07:00Z" }),
      command({ id: "c2", body: "in between", createdAt: "2026-10-05T10:00:00Z" }),
    ]);
    renderAt("/w/w1/sessions/s1", route);

    expect(await screen.findByText("in between", { selector: ".chat p" })).toBeInTheDocument();
    const stop = document.querySelector('.chat-row[data-kind="command"] strong');
    expect(stop).toHaveTextContent("Stop the session");
    expect(lines().at(-1)).toBe("command"); // the stop, last
    expect(lines().filter((k) => k === "command")).toHaveLength(2);
  });

  it("puts the box to type in under the conversation, and in the raw view keeps it in the commands card", async () => {
    setup([]);
    renderAt("/w/w1/sessions/s1", route);

    const box = await screen.findByPlaceholderText(/A message for Claude/);
    expect(box.closest(".chat-card")).not.toBeNull();
    expect(screen.getAllByPlaceholderText(/A message for Claude/)).toHaveLength(1);

    await userEvent.click(screen.getByRole("button", { name: "Raw events" }));
    const raw = screen.getByPlaceholderText(/A message for Claude/);
    expect(raw.closest(".chat-card")).toBeNull();
    expect(raw.closest("section")).toHaveTextContent("Send to this session");
  });
});
