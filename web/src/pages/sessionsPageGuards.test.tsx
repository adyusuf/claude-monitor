import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionRow } from "../api/types";
import { FakeEventSource, ME, mockApi, openMachine, renderAt } from "../test/helpers";
import { SessionsPage } from "./SessionsPage";

const row = (over: Partial<SessionRow> = {}): SessionRow => ({
  id: "s1", harnessKind: "claude_code", title: "Fix the login", model: null, gitBranch: "main", status: "active", startedAt: "2026-10-05T10:00:00Z",
  lastEventAt: "2026-10-05T10:00:00Z", endedAt: null, projectId: "p", projectName: "widgets", agentId: "a1", hostname: "laptop-1",
  ownerId: "u1", ownerName: "Owner", costUsd: null, openPermissions: 0, ...over,
});

const route = [{ path: "/w/:ws/sessions", element: <SessionsPage /> }];
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** Answers the sessions list through `sessions`; the person, the machines and the agents are the plain ones. */
const serve = (sessions: (url: URL) => Promise<Response> | Response) =>
  vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
    const url = new URL(String(input), "http://test.local");
    const path = url.pathname.replace(/^\/api/, "");
    if (path === "/me") return json(ME);
    if (path === "/workspaces/w1/sessions") return sessions(url);
    return json({ title: "not_found" }, 404);
  });

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("a slow answer for text the person has since changed", () => {
  it("does not overwrite the list of the newer search", async () => {
    let releaseOld: () => void = () => undefined;
    const asked: string[] = [];
    serve((url) => {
      const q = url.searchParams.get("q") ?? "";
      asked.push(q);
      if (q === "a") return new Promise((resolve) => { releaseOld = () => resolve(json({ items: [row({ id: "old", title: "Old result" })], next: null })); });
      return json({ items: [row({ id: "new", title: q === "ab" ? "New result" : "Everything" })], next: null });
    });
    renderAt("/w/w1/sessions", route);
    await openMachine("laptop-1");
    await userEvent.type(screen.getByRole("searchbox"), "a");
    await waitFor(() => expect(asked).toContain("a"));
    await userEvent.type(screen.getByRole("searchbox"), "b");
    expect(await screen.findByText("New result")).toBeInTheDocument();
    await act(async () => releaseOld());
    expect(screen.queryByText("Old result")).toBeNull();
    expect(screen.getByText("New result")).toBeInTheDocument();
  });

  it("does not append the next page of the previous search to the newer list", async () => {
    let releaseMore: () => void = () => undefined;
    serve((url) => {
      const q = url.searchParams.get("q") ?? "";
      if (url.searchParams.get("cursor") === "c1") {
        return new Promise((resolve) => { releaseMore = () => resolve(json({ items: [row({ id: "late", title: "Late page row" })], next: null })); });
      }
      return json(q === "x" ? { items: [row({ id: "x1", title: "Match for x" })], next: null } : { items: [row()], next: "c1" });
    });
    renderAt("/w/w1/sessions", route);
    await openMachine("laptop-1");
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    await userEvent.type(screen.getByRole("searchbox"), "x");
    expect(await screen.findByText("Match for x")).toBeInTheDocument();
    await act(async () => releaseMore());
    expect(screen.queryByText("Late page row")).toBeNull();
    expect(screen.getByText("Match for x")).toBeInTheDocument();
  });
});

describe("loading the next page when the server fails", () => {
  it("keeps the sessions already shown and shows the error, and the person can try again", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/sessions": (call) => (call.path.includes("cursor=c1") ? { status: 500 } : { body: { items: [row()], next: "c1" } }),
    });
    renderAt("/w/w1/sessions", route);
    await openMachine("laptop-1");
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByText("Fix the login")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument();
  });
});
