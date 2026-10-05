import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CommandRow } from "../api/types";
import { config } from "../config";
import { ME, mockApi, renderAt } from "../test/helpers";
import { CommandItem } from "./CommandHistory";
import { messageText, replyState } from "./CommandReply";

const NOW = Date.parse("2026-10-05T12:00:00Z");
const minutesAgo = (m: number) => new Date(NOW - m * 60_000).toISOString();

const command = (over: Partial<CommandRow> = {}): CommandRow => ({
  id: "c1", kind: "prompt", body: "Run the tests", status: "applied", createdBy: "u1", createdAt: minutesAgo(5),
  expiresAt: minutesAgo(-25), deliveredAt: minutesAgo(4), appliedAt: minutesAgo(1), result: null, ...over,
});

afterEach(() => vi.restoreAllMocks());

describe("where a command stands once it was handed to the session", () => {
  it("is answered when a reply was found, whatever its age", () => {
    expect(replyState(command({ replyEventId: 7, appliedAt: minutesAgo(600) }), false, NOW)).toBe("answered");
    expect(replyState(command({ replyEventId: 7 }), true, NOW)).toBe("answered");
  });

  it("is waiting for a reply until the wait limit, then no reply is recorded", () => {
    expect(replyState(command({ appliedAt: minutesAgo(config.replyWaitMinutes - 1) }), false, NOW)).toBe("waiting");
    expect(replyState(command({ appliedAt: minutesAgo(config.replyWaitMinutes + 1) }), false, NOW)).toBe("unrecorded");
    expect(replyState(command({ replyEventId: null }), false, NOW)).toBe("waiting"); // an older API says nothing: still waiting
    expect(replyState(command({ appliedAt: null }), false, NOW)).toBe("waiting");
  });

  it("owes nothing once the session has ended, and only an applied prompt has one", () => {
    expect(replyState(command(), true, NOW)).toBeNull();
    expect(replyState(command({ status: "delivered" }), false, NOW)).toBeNull();
    expect(replyState(command({ status: "queued" }), false, NOW)).toBeNull();
    expect(replyState(command({ kind: "stop", body: null }), false, NOW)).toBeNull();
  });

  it("reads the text blocks of an assistant message and nothing else", () => {
    const payload = { message: { content: [{ type: "thinking", thinking: "no" }, { type: "text", text: "one" }, { type: "tool_use" }, { type: "text", text: "two" }] } };
    expect(messageText(payload)).toBe("one\n\ntwo");
    expect(messageText({})).toBe("");
    expect(messageText({ message: { content: "plain" } })).toBe("");
  });
});

describe("under a command", () => {
  const show = (c: CommandRow, ended = false) => {
    vi.spyOn(Date, "now").mockReturnValue(NOW);
    return renderAt("/x", [{ path: "/x", element: <ul><CommandItem command={c} sessionId="s1" ended={ended} now={NOW} canCancel onCancel={() => undefined} /></ul> }]);
  };

  it("shows Applied and the answer apart: the badge stays, the reply is its own part", async () => {
    mockApi({ "GET /me": { body: ME } });
    show(command({ replyEventId: 7, replyText: "All 12 tests pass.", replyMore: false }));
    const li = screen.getByText("Run the tests").closest("li")!;
    expect(within(li).getByText("Applied")).toBeInTheDocument();
    expect(within(li).getByText("Claude replied")).toBeInTheDocument();
    expect(within(li).getByText("All 12 tests pass.")).toBeInTheDocument();
    expect(within(li).queryByRole("button", { name: "See the whole reply" })).not.toBeInTheDocument(); // nothing more to see
    expect(within(li).queryByText("Waiting for Claude's reply")).not.toBeInTheDocument();
  });

  it("says waiting while there is no reply, and nothing for a stop", () => {
    show(command());
    expect(screen.getByText("Waiting for Claude's reply")).toBeInTheDocument();
    expect(screen.queryByText("Claude replied")).not.toBeInTheDocument();
  });

  it("says no reply is recorded once it has waited too long", () => {
    show(command({ appliedAt: minutesAgo(config.replyWaitMinutes + 5) }));
    expect(screen.getByText("No reply recorded")).toBeInTheDocument();
  });

  it("shows nothing about a reply for a stop or a session that ended", () => {
    show(command({ kind: "stop", body: null }), true);
    expect(screen.queryByText(/reply/i)).not.toBeInTheDocument();
  });

  it("loads the whole reply on request and folds it back", async () => {
    const full = "First paragraph of the answer.\n\nSecond paragraph, which the preview cut off.";
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1/events": {
        body: { items: [{ id: 7, kind: "transcript", occurredAt: "", truncated: false, payload: { message: { content: [{ type: "text", text: full }] } } }], next: null },
      },
    });
    show(command({ replyEventId: 7, replyText: "First paragraph of the answer.", replyMore: true }));
    expect(screen.getByText("First paragraph of the answer.…")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "See the whole reply" }));
    expect(await screen.findByText(/Second paragraph, which the preview cut off\./)).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/sessions/s1/events"))[0]!.path).toBe("/sessions/s1/events?before=8&limit=1");
    await userEvent.click(screen.getByRole("button", { name: "Show less" }));
    expect(screen.queryByText(/Second paragraph/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "See the whole reply" }));
    expect(await screen.findByText(/Second paragraph/)).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/sessions/s1/events"))).toHaveLength(1); // read once
  });

  it("says so when the whole reply can no longer be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /sessions/s1/events": { body: { items: [], next: null } } });
    show(command({ replyEventId: 7, replyText: "Start", replyMore: true }));
    await userEvent.click(screen.getByRole("button", { name: "See the whole reply" }));
    expect(await screen.findByText("The reply is no longer available.")).toBeInTheDocument();
  });
});
