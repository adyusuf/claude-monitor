import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionRow } from "../api/types";
import { config } from "../config";
import { FakeEventSource, ME, mockApi, openMachine, renderAt } from "../test/helpers";
import { SessionsPage } from "./SessionsPage";

const row = (over: Partial<SessionRow> = {}): SessionRow => ({
  id: "s1", harnessKind: "claude_code", title: "Fix the login", model: null, gitBranch: "main", status: "ended", startedAt: "2026-10-05T10:00:00Z",
  lastEventAt: "2026-10-05T10:00:00Z", endedAt: "2026-10-05T11:00:00Z", projectId: "p", projectName: "widgets", agentId: "a1", hostname: "laptop-1",
  ownerId: "u1", ownerName: "Owner", costUsd: null, openPermissions: 0, ...over,
});

const route = [{ path: "/w/:ws/sessions", element: <SessionsPage /> }];
const open = async () => {
  renderAt("/w/w1/sessions", route);
  await openMachine("laptop-1");
};

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("removing an ended session from the list", () => {
  it("asks first, posts the hide for that session, and reads the list again so the session is gone", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    let hidden = false;
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": () => ({ body: { items: hidden ? [] : [row()], next: null } }),
      "POST /sessions/s1/hide": () => { hidden = true; return { status: 204 }; },
    });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Remove from the list" }));
    expect(confirm).toHaveBeenCalledWith("Remove this ended session from the list? Nothing is deleted, and it comes back if it reports again.");
    await waitFor(() => expect(screen.queryByText("Fix the login")).toBeNull());
    expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual(["/sessions/s1/hide"]);
  });

  it("does nothing when the person declines the confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { body: { items: [row()], next: null } } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Remove from the list" }));
    expect(calls.filter((c) => c.method === "POST")).toEqual([]);
    expect(screen.getByText("Fix the login")).toBeInTheDocument();
  });

  it("shows the API's refusal under the session and keeps it listed", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [row()], next: null } },
      "POST /sessions/s1/hide": { status: 400, body: { title: "session_not_ended" } },
    });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Remove from the list" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Only an ended session can be removed from the list.");
    expect(screen.getByText("Fix the login")).toBeInTheDocument();
  });

  it("offers the removal for an ended session of the signed-in person only, not for a running one or someone else's", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [
        row({ id: "mine-ended", title: "Mine ended" }),
        row({ id: "mine-running", title: "Mine running", status: "active", endedAt: null }),
        row({ id: "theirs-ended", title: "Theirs ended", ownerId: "u2" }),
      ], next: null } },
    });
    await open();
    await screen.findByText("Mine ended");
    const item = (title: string) => screen.getByText(title).closest(".session-row") as HTMLElement;
    expect(within(item("Mine ended")).getByRole("button", { name: "Remove from the list" })).toBeInTheDocument();
    expect(within(item("Mine running")).queryByRole("button", { name: "Remove from the list" })).toBeNull();
    expect(within(item("Theirs ended")).queryByRole("button", { name: "Remove from the list" })).toBeNull();
  });
});

describe("stopping a session the web started", () => {
  const running = (over: Partial<SessionRow> = {}) => row({ status: "active", endedAt: null, startedFromWeb: true, webSessionRef: "ref-1", ...over });

  it("asks first, requests a claude_stop for that session's machine and says the stop was sent, and the button stays off", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [running()], next: null } },
      "POST /agents/a1/actions": { status: 201, body: { id: "act1" } },
    });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Stop" }));
    expect(confirm).toHaveBeenCalledWith("Stop this session? Its conversation is kept.");
    expect(await screen.findByText(/^Stop sent\./)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")).toMatchObject({ path: "/agents/a1/actions", body: { kind: "claude_stop", sessionRef: "ref-1" } });
    expect(screen.getByRole("button", { name: "Stop" })).toBeDisabled();
  });

  describe("after the action queue's lifetime", () => {
    beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
    afterEach(() => vi.useRealTimers());

    it("stays off until the action queue's lifetime has passed, then can be pressed again and the notice goes", async () => {
      vi.spyOn(window, "confirm").mockReturnValue(true);
      const calls = mockApi({
        "GET /me": { body: ME },
        "GET /workspaces/w1/sessions": { body: { items: [running()], next: null } },
        "POST /agents/a1/actions": { status: 201, body: { id: "act1" } },
      });
      await open();
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(await screen.findByRole("button", { name: "Stop" }));
      expect(await screen.findByText(/^Stop sent\./)).toBeInTheDocument();

      act(() => vi.advanceTimersByTime(config.agentActionQueueMs - 1_000));
      expect(screen.getByRole("button", { name: "Stop" })).toBeDisabled();
      act(() => vi.advanceTimersByTime(1_000));
      expect(screen.getByRole("button", { name: "Stop" })).toBeEnabled();
      expect(screen.queryByText(/^Stop sent\./)).toBeNull();

      await user.click(screen.getByRole("button", { name: "Stop" }));
      await waitFor(() => expect(calls.filter((c) => c.method === "POST")).toHaveLength(2));
    });
  });

  it("sends nothing when the person declines the confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { body: { items: [running()], next: null } } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Stop" }));
    expect(calls.filter((c) => c.method === "POST")).toEqual([]);
    expect(screen.queryByText(/^Stop sent\./)).toBeNull();
  });

  it("shows the error and not 'Stop sent' when the request fails, and the button can be pressed again", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [running()], next: null } },
      "POST /agents/a1/actions": { status: 403, body: {} },
    });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Stop" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
    expect(screen.queryByText(/^Stop sent\./)).toBeNull();
    expect(screen.getByRole("button", { name: "Stop" })).toBeEnabled();
  });

  it("offers the stop only to the owner of a session the web started, and not when it has no start reference", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [
        running({ id: "mine", title: "Mine" }),
        running({ id: "theirs", title: "Theirs", ownerId: "u2" }),
        running({ id: "noref", title: "No ref", webSessionRef: null }),
        row({ id: "local", title: "Local", status: "active", endedAt: null }),
      ], next: null } },
    });
    await open();
    await screen.findByText("Mine");
    const item = (title: string) => screen.getByText(title).closest(".session-row") as HTMLElement;
    expect(within(item("Mine")).getByRole("button", { name: "Stop" })).toBeInTheDocument();
    for (const title of ["Theirs", "No ref", "Local"]) expect(within(item(title)).queryByRole("button", { name: "Stop" })).toBeNull();
  });
});
