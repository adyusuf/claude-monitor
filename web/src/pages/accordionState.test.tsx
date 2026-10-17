import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EventRow } from "../api/types";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, back, call } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const detail = {
  session: { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
    projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 },
  canCommand: true, tasks: [], subagents: [], usage: [],
};
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];

/** A tool call and its result (ids n and n+1). */
const tool = (n: number, name = "Bash", input: Record<string, unknown> = { command: `run ${n}` }) => [call(n, `t${n}`, name, input), back(n + 1, `t${n}`, `out ${n}`)];

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("what the reader opened stays open while the page updates", () => {
  it("keeps a run of tool calls and a call inside it open when newer calls arrive and when older ones are read on top", async () => {
    let newer: EventRow[] = [];
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": (c) => c.path.includes("before=")
        ? { body: { items: [...tool(1)].reverse(), next: null } }
        : { body: { items: [...newer, ...[...tool(3), ...tool(5)].reverse()], next: "3" } },
    });
    renderAt("/w/w1/sessions/s1", route);
    const summary = await screen.findByText("2 tool calls");
    const run = () => document.querySelector<HTMLDetailsElement>(".chat-tool-run")!;
    await userEvent.click(summary);
    await waitFor(() => expect(run().open).toBe(true));
    await userEvent.click(within(run()).getByText("run 5"));
    await waitFor(() => expect(within(run()).getByText("out 5")).toBeVisible());

    newer = [...tool(7)].reverse();
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    expect(await screen.findByText("3 tool calls")).toBeInTheDocument();
    expect(run().open).toBe(true);
    expect(within(run()).getByText("out 5")).toBeVisible(); // the call opened inside it too

    await userEvent.click(screen.getByRole("button", { name: "Older activity" }));
    expect(await screen.findByText("4 tool calls")).toBeInTheDocument();
    expect(run().open).toBe(true);
    expect(within(run()).getByText("out 5")).toBeVisible();
  });

  it("stays closed when the reader closed it, and a closed run is still closed after an update", async () => {
    let newer: EventRow[] = [];
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": () => ({ body: { items: [...newer, ...[...tool(1), ...tool(3)].reverse()], next: null } }),
    });
    renderAt("/w/w1/sessions/s1", route);
    const summary = await screen.findByText("2 tool calls");
    const run = () => document.querySelector<HTMLDetailsElement>(".chat-tool-run")!;
    await userEvent.click(summary);
    await waitFor(() => expect(run().open).toBe(true));
    await userEvent.click(summary);
    await waitFor(() => expect(run().open).toBe(false));
    newer = [...tool(5)].reverse();
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    expect(await screen.findByText("3 tool calls")).toBeInTheDocument(); // the update arrived
    expect(run().open).toBe(false);
  });
});

describe("the raw events keep what was read and what was opened", () => {
  const prompt = (id: number, text: string): EventRow => ({ id, kind: "hook:UserPromptSubmit", occurredAt: at, truncated: false, payload: { prompt: text } });
  const pre = (id: number): EventRow => ({ id, kind: "hook:PreToolUse", occurredAt: at, truncated: false, payload: { tool_name: "Bash", tool_input: { command: `cmd ${id}` } } });

  async function openRaw(events: (() => EventRow[]), older: EventRow[]) {
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: detail },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": (c) => c.path.includes("before=") ? { body: { items: older, next: null } } : { body: { items: events(), next: "x" } },
    });
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: en.chat.view_raw }));
  }

  it("keeps the older activity that was read when a live update arrives", async () => {
    let fresh: EventRow[] = [prompt(30, "newest prompt")];
    await openRaw(() => fresh, [prompt(10, "an older prompt")]);
    await screen.findByText("newest prompt");
    await userEvent.click(screen.getByRole("button", { name: "Older activity" }));
    await screen.findByText("an older prompt");

    fresh = [prompt(40, "even newer prompt"), prompt(30, "newest prompt")];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    await screen.findByText("even newer prompt");
    expect(screen.getByText("an older prompt")).toBeInTheDocument();
    expect(screen.getByText("newest prompt")).toBeInTheDocument();
  });

  it("keeps a group of tool calls open when it grows, and an input open or closed as the reader left it", async () => {
    let fresh: EventRow[] = [pre(22), pre(21), prompt(20, "a long prompt\nwith a second line")];
    await openRaw(() => fresh, []);
    const toggle = await screen.findByRole("button", { name: /Tool calls: 2/ });
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");

    const input = () => document.querySelector<HTMLElement>(".act-input .act-text")!;
    expect(input()).toHaveClass("act-preview"); // closed: a short preview
    await userEvent.click(input());
    expect(input()).not.toHaveClass("act-preview"); // open: the whole text

    fresh = [pre(24), pre(23), pre(22), pre(21), prompt(20, "a long prompt\nwith a second line")];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    const grown = await screen.findByRole("button", { name: /Tool calls: 4/ });
    expect(grown).toHaveAttribute("aria-expanded", "true");
    expect(input()).not.toHaveClass("act-preview");
  });
});
