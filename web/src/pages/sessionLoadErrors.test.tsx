import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CommandRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, said } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const detail = {
  session: { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
    projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 },
  canCommand: true, tasks: [], subagents: [], usage: [],
};
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];
const queued: CommandRow = {
  id: "c1", kind: "prompt", body: "earlier message", status: "queued", createdBy: "u1", createdAt: new Date().toISOString(),
  expiresAt: new Date(Date.now() + 600_000).toISOString(), deliveredAt: null, appliedAt: null, result: null,
};
const base = {
  "GET /me": { body: ME },
  "GET /sessions/s1": { body: detail },
  "GET /sessions/s1/permission-requests": { body: [] },
};

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("what the session page shows when a read fails", () => {
  it("keeps the commands it had and shows the error when the commands cannot be read again", async () => {
    let failing = false;
    mockApi({
      ...base,
      "GET /sessions/s1/events": { body: { items: [said(3, "Done.")], next: null } },
      "GET /sessions/s1/commands": () => (failing ? { status: 500 } : { body: [queued] }),
    });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("earlier message")).toBeInTheDocument();
    failing = true;
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.getByText("earlier message")).toBeInTheDocument();
  });

  it("says the activity cannot be read, and not that there is none, when the first read fails", async () => {
    mockApi({ ...base, "GET /sessions/s1/events": { status: 500 }, "GET /sessions/s1/commands": { body: [] } });
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.queryByText("No activity recorded.")).toBeNull();
  });

  it("keeps the conversation and shows the error when an older page cannot be read", async () => {
    mockApi({
      ...base,
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/events": (c) => (c.path.includes("before=") ? { status: 500 } : { body: { items: [said(3, "Done.")], next: "3" } }),
    });
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Older activity" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.getByText("Done.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Older activity" })).toBeInTheDocument(); // the person can try again
  });

  it("shows the error in the raw view too, and not that there is no activity", async () => {
    mockApi({ ...base, "GET /sessions/s1/events": { status: 500 }, "GET /sessions/s1/commands": { body: [] } });
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Raw events" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.queryByText("No activity recorded.")).toBeNull();
  });
});

describe("cancelling a queued message from the conversation", () => {
  it("shows the API's refusal instead of swallowing it", async () => {
    mockApi({
      ...base,
      "GET /sessions/s1/events": { body: { items: [said(3, "Done.")], next: null } },
      "GET /sessions/s1/commands": { body: [queued] },
      "POST /commands/c1/cancel": { status: 409, body: {} },
    });
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That has already changed. Refresh and try again.");
  });

  it("shows no error when the cancel works", async () => {
    mockApi({
      ...base,
      "GET /sessions/s1/events": { body: { items: [said(3, "Done.")], next: null } },
      "GET /sessions/s1/commands": { body: [queued] },
      "POST /commands/c1/cancel": { status: 204 },
    });
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    await screen.findByText("earlier message");
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
