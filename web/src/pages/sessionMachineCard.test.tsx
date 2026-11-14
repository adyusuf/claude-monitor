import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionRow } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { SessionsPage } from "./SessionsPage";
import { text } from "./machinesFixture";

const row = (id: string, hostname: string, over: Partial<SessionRow> = {}): SessionRow => ({
  id, harnessKind: "claude_code", title: `Title ${id}`, model: null, gitBranch: "main", status: "idle", startedAt: "2026-10-05T10:00:00Z",
  lastEventAt: "2026-10-05T10:00:00Z", endedAt: null, projectId: "p", projectName: "widgets", agentId: `agent-${hostname}`, hostname, ownerId: "u1",
  ownerName: "Owner", costUsd: null, openPermissions: 0, ...over,
});

const route = [{ path: "/w/:ws/sessions", element: <SessionsPage /> }];
const card = (host: string) => [...document.querySelectorAll<HTMLElement>(".machine-card")].find((c) => c.querySelector(".machine-card-name")?.textContent?.includes(host))!;
/** The card's three parts as spaced text: its name, the active count and the muted line. */
const figures = (host: string) => [...card(host).children].map(text).join(" | ");

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("a machine's card on the sessions page", () => {
  const page = (next: string | null) => mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/sessions": { body: { items: [
      row("a", "alpha", { status: "active" }), row("b", "alpha", { status: "active" }), row("c", "alpha", { status: "waiting" }),
      row("d", "alpha", { status: "idle" }), row("e", "alpha", { status: "ended" }),
    ], next } },
  });

  it("shows how many sessions are active now, how many wait for an answer and how many there are, with no plus once every session is loaded", async () => {
    page(null);
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Select a machine to see its sessions.");
    expect(figures("alpha")).toBe("alpha | 2 active now | 1 waiting · 5 in total");
  });

  it("puts a plus after every count (active, waiting and total) while more sessions are still to be loaded, because each is then a floor", async () => {
    page("c1");
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Select a machine to see its sessions.");
    expect(figures("alpha")).toBe("alpha | 2+ active now | 1+ waiting · 5+ in total");
  });

  it("puts a plus after the count badge in the opened machine's head while more sessions are still to be loaded", async () => {
    page("c1");
    renderAt("/w/w1/sessions", route);
    await userEvent.click(await screen.findByRole("button", { expanded: false }));
    const badge = within(document.querySelector(".session-machine-head")!).getByTitle("5+ session(s)");
    expect(badge).toHaveTextContent(/^5\+$/);
  });

  it("leaves the waiting part out when no session waits", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { body: { items: [row("a", "alpha", { status: "active" })], next: null } } });
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Select a machine to see its sessions.");
    expect(figures("alpha")).toBe("alpha | 1 active now | 1 in total");
  });

  it("opens the machine's sessions when pressed and closes them when pressed again", async () => {
    page(null);
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Select a machine to see its sessions.");
    expect(card("alpha")).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("Title a")).toBeNull();

    await userEvent.click(card("alpha"));
    expect(card("alpha")).toHaveAttribute("aria-expanded", "true");
    expect(await screen.findByText("Title a")).toBeInTheDocument();
    expect(screen.queryByText("Select a machine to see its sessions.")).toBeNull();

    await userEvent.click(card("alpha"));
    expect(card("alpha")).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("Title a")).toBeNull();
    expect(screen.getByText("Select a machine to see its sessions.")).toBeInTheDocument();
  });

  it("opens only the pressed card and leaves another machine's card closed", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { body: { items: [row("a", "alpha"), row("b", "beta")], next: null } } });
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Select a machine to see its sessions.");
    await userEvent.click(card("alpha"));
    expect(card("beta")).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText("Title a")).toBeInTheDocument();
    expect(screen.queryByText("Title b")).toBeNull();
  });

  it("opens the cards that match a search without a click, and lists only the machines with a match", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/machines": { body: [machine({ agentId: "agent-alpha", hostname: "alpha" }), machine({ agentId: "agent-beta", hostname: "beta" })] },
      "GET /workspaces/w1/sessions": (c) => ({ body: { items: c.path.includes("q=login") ? [row("a", "alpha", { title: "Fix the login" })] : [row("a", "alpha"), row("b", "beta")], next: null } }),
    });
    renderAt("/w/w1/sessions", route);
    await screen.findByText("Select a machine to see its sessions.");
    expect(card("alpha")).toHaveAttribute("aria-expanded", "false");
    expect(card("beta")).toBeDefined();

    await userEvent.type(screen.getByRole("searchbox"), "login");
    expect(await screen.findByText("Fix the login")).toBeInTheDocument();
    expect(card("alpha")).toHaveAttribute("aria-expanded", "true");
    expect(card("beta")).toBeUndefined();
    expect(within(document.querySelector(".session-machine")!).getByRole("heading", { level: 2 })).toHaveTextContent("alpha");
  });
});
