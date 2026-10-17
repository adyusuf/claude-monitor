import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, call, user } from "../test/transcript-fixtures";
import { NEXT_STEP_TOOL } from "../lib/transcript";
import { SessionPage } from "./SessionPage";

const session = { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
  projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 };
const detail = (canCommand: boolean) => ({ session, canCommand, tasks: [], subagents: [], usage: [] });
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];
const question = call(2, "t1", NEXT_STEP_TOOL, {
  recap: "Merged.", ask: "Push it to prod?", actions: [{ label: "Yes, push", message: "Yes, push it to prod." }, { label: "Not now" }],
});

function setup(canCommand: boolean, events: () => EventRow[]) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /sessions/s1": { body: detail(canCommand) },
    "GET /sessions/s1/commands": { body: [] },
    "GET /sessions/s1/permission-requests": { body: [] },
    "GET /sessions/s1/events": () => ({ body: { items: events(), next: null } }),
    "POST /sessions/s1/commands": { body: { id: "c1" } },
  });
}
const posted = (calls: ReturnType<typeof mockApi>) => calls.filter((c) => c.method === "POST" && c.path === "/sessions/s1/commands").map((c) => c.body);

describe("answering the desktop app's next-step question from the web", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("shows the question with its buttons and sends the button's message to the session as a prompt", async () => {
    const calls = setup(true, () => [question, user(1, "go")]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("Push it to prod?")).toBeInTheDocument();
    expect(screen.getByText("Merged.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Yes, push" }));

    await waitFor(() => expect(posted(calls)).toEqual([{ kind: "prompt", body: "Yes, push it to prod." }]));
    expect(await screen.findByText(/Sent to the session: Yes, push it to prod\./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Yes, push" })).toBeNull(); // answered once; no second click
  });

  it("answers a button that has no message with its label, and takes a typed answer", async () => {
    const calls = setup(true, () => [question]);
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Not now" }));
    await waitFor(() => expect(posted(calls)).toEqual([{ kind: "prompt", body: "Not now" }]));
  });

  it("sends a typed answer and keeps the send button off while the box is empty", async () => {
    const calls = setup(true, () => [question]);
    renderAt("/w/w1/sessions/s1", route);
    const card = within((await screen.findByText("Push it to prod?")).closest(".chat-nextstep") as HTMLElement); // the commands box below has a Send button too
    const send = card.getByRole("button", { name: "Send" });
    expect(send).toBeDisabled();
    await userEvent.type(card.getByPlaceholderText("Or type an answer"), "  after lunch  ");
    await userEvent.click(send);
    await waitFor(() => expect(posted(calls)).toEqual([{ kind: "prompt", body: "after lunch" }]));
  });

  it("shows no way to answer to someone who may not command the session", async () => {
    const calls = setup(false, () => [question]);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("Push it to prod?")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Yes, push" })).toBeNull();
    expect(screen.queryByPlaceholderText("Or type an answer")).toBeNull();
    expect(screen.getByText("Waiting for the person at this session")).toBeInTheDocument();
    expect(posted(calls)).toEqual([]);
  });

  it("closes the question once the session heard from the user, and offers no answer on an old one", async () => {
    let events: EventRow[] = [question];
    const calls = setup(true, () => events);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByRole("button", { name: "Yes, push" })).toBeInTheDocument();

    events = [user(3, "Yes, push it to prod."), question];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));

    expect(await screen.findByText("Answered")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Yes, push" })).toBeNull();
    expect(posted(calls)).toEqual([]);
  });

  it("says so when the answer could not be sent and lets the person try again", async () => {
    let fail = true;
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail(true) },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [question], next: null } },
      "POST /sessions/s1/commands": () => (fail ? { status: 500, body: { title: "boom" } } : { body: { id: "c1" } }),
    });
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Yes, push" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The answer was not sent");

    fail = false;
    await userEvent.click(screen.getByRole("button", { name: "Yes, push" }));
    expect(await screen.findByText(/Sent to the session/)).toBeInTheDocument();
  });
});
