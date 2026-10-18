import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { SessionsPage } from "./SessionsPage";

const row = (id: string, hostname: string, gitBranch: string | null, lastEventAt: string, title = `Title ${id}`): SessionRow => ({
  id, harnessKind: "claude_code", title, model: null, gitBranch, status: "idle", startedAt: lastEventAt, lastEventAt, endedAt: null,
  projectId: "p", projectName: "widgets", agentId: "a", hostname, ownerId: "u1", ownerName: "Owner", costUsd: null, openPermissions: 0,
});

const route = [{ path: "/w/:ws/sessions", element: <SessionsPage /> }];
const titles = (machine: HTMLElement) => [...machine.querySelectorAll(".title-text")].map((e) => e.textContent);
const machineOf = (name: string) => document.querySelector<HTMLElement>(`.session-machine[aria-label="${name}"]`)!;

describe("the sessions list grouped by machine and branch", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("keeps the machines in a fixed order by name, and shows each one's branches and sessions with the most recent activity first", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [
        row("old", "alpha", "main", "2026-10-05T08:00:00Z"),
        row("mid", "zulu", "feature/x", "2026-10-05T12:30:00Z"), // the machine with the newest activity is not first
        row("new", "alpha", "main", "2026-10-05T12:00:00Z"),
        row("side", "alpha", null, "2026-10-05T09:00:00Z"),
      ], next: null } },
    });
    renderAt("/w/w1/sessions", route);
    expect(await screen.findByText("Title new")).toBeInTheDocument();

    const machines = [...document.querySelectorAll<HTMLElement>(".session-machine")];
    expect(machines.map((m) => m.getAttribute("aria-label"))).toEqual(["alpha", "zulu"]);
    const alpha = within(machines[0]!);
    expect(alpha.getAllByRole("heading", { level: 3 }).map((h) => h.textContent)).toEqual(["⎇ main", "⎇ No branch reported"]);
    expect(titles(machines[0]!)).toEqual(["Title new", "Title old", "Title side"]);
    expect(within(machines[1]!).getByRole("heading", { level: 3 })).toHaveTextContent("feature/x");
    expect(alpha.getByRole("heading", { level: 2 })).toHaveTextContent("alpha");
    expect(alpha.getByRole("heading", { level: 2 })).toHaveTextContent("3");
  });

  it("goes on updating a machine's cards while the pointer is on it but moves nothing in it, and sorts again when the pointer leaves", async () => {
    let current = [
      row("a1", "alpha", "main", "2026-10-05T10:00:00Z"),
      row("a2", "alpha", "main", "2026-10-05T09:00:00Z"),
      row("b1", "beta", "main", "2026-10-05T08:00:00Z"),
      row("b2", "beta", "main", "2026-10-05T07:00:00Z"),
    ];
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": () => ({ body: { items: current, next: null } }) });
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Title a1");
    expect(titles(machineOf("alpha"))).toEqual(["Title a1", "Title a2"]);

    await userEvent.hover(machineOf("alpha"));
    current = [
      row("a1", "alpha", "main", "2026-10-05T10:00:00Z"),
      row("a2", "alpha", "main", "2026-10-05T11:00:00Z", "a2 renamed"), // a2 is now the most recent
      row("a3", "alpha", "main", "2026-10-05T12:00:00Z"), // and a new session appeared
      row("b1", "beta", "main", "2026-10-05T08:00:00Z"),
      row("b2", "beta", "main", "2026-10-05T13:00:00Z"), // the other machine is not pointed at
    ];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "a2" }));
    await screen.findByText("a2 renamed"); // the new data arrived
    expect(titles(machineOf("alpha"))).toEqual(["Title a1", "a2 renamed", "Title a3"]); // in the order the reader saw, the newcomer after
    await waitFor(() => expect(titles(machineOf("beta"))).toEqual(["Title b2", "Title b1"])); // another machine moves freely
    expect([...document.querySelectorAll(".session-machine")].map((m) => m.getAttribute("aria-label"))).toEqual(["alpha", "beta"]);

    await userEvent.unhover(machineOf("alpha"));
    expect(titles(machineOf("alpha"))).toEqual(["Title a3", "a2 renamed", "Title a1"]);
  });

  it("keeps the order while the keyboard focus is inside a machine too, until it leaves", async () => {
    let current = [row("a1", "alpha", "main", "2026-10-05T10:00:00Z"), row("a2", "alpha", "main", "2026-10-05T09:00:00Z")];
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": () => ({ body: { items: current, next: null } }) });
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Title a1");

    act(() => screen.getByText("Title a1").closest("a")!.focus()); // the keyboard reader is on a card of the machine
    expect(machineOf("alpha").contains(document.activeElement)).toBe(true);
    current = [row("a1", "alpha", "main", "2026-10-05T10:00:00Z"), row("a2", "alpha", "main", "2026-10-05T11:00:00Z", "a2 renamed")];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "a2" }));
    await screen.findByText("a2 renamed");
    expect(titles(machineOf("alpha"))).toEqual(["Title a1", "a2 renamed"]);

    (document.activeElement as HTMLElement).blur();
    await waitFor(() => expect(titles(machineOf("alpha"))).toEqual(["a2 renamed", "Title a1"]));
  });
});
