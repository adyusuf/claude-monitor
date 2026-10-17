import { screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { SessionsPage } from "./SessionsPage";

const row = (id: string, hostname: string, gitBranch: string | null, lastEventAt: string): SessionRow => ({
  id, harnessKind: "claude_code", title: `Title ${id}`, model: null, gitBranch, status: "idle", startedAt: lastEventAt, lastEventAt, endedAt: null,
  projectId: "p", projectName: "widgets", agentId: "a", hostname, ownerId: "u1", ownerName: "Owner", costUsd: null, openPermissions: 0,
});

describe("the sessions list grouped by machine and branch", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("shows each machine with its branches, the most recently active first, and the sessions of a branch newest first", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": { body: { items: [
        row("old", "alpha", "main", "2026-10-05T08:00:00Z"),
        row("mid", "beta", "feature/x", "2026-10-05T10:00:00Z"),
        row("new", "alpha", "main", "2026-10-05T12:00:00Z"),
        row("side", "alpha", null, "2026-10-05T09:00:00Z"),
      ], next: null } },
    });
    renderAt("/w/w1/sessions", [{ path: "/w/:ws/sessions", element: <SessionsPage /> }]);
    expect(await screen.findByText("Title new")).toBeInTheDocument();

    const machines = [...document.querySelectorAll<HTMLElement>(".session-machine")];
    expect(machines.map((m) => m.getAttribute("aria-label"))).toEqual(["alpha", "beta"]);
    const alpha = within(machines[0]!);
    expect(alpha.getAllByRole("heading", { level: 3 }).map((h) => h.textContent)).toEqual(["⎇ main", "⎇ No branch reported"]);
    expect([...machines[0]!.querySelectorAll(".title-text")].map((e) => e.textContent)).toEqual(["Title new", "Title old", "Title side"]);
    expect(within(machines[1]!).getByRole("heading", { level: 3 })).toHaveTextContent("feature/x");
    expect(within(machines[0]!).getByRole("heading", { level: 2 })).toHaveTextContent("alpha");
    expect(within(machines[0]!).getByRole("heading", { level: 2 })).toHaveTextContent("3");
  });
});
