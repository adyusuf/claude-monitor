import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { Invitation, Member } from "../api/types";
import { ME, mockApi, renderAt, type Call } from "../test/helpers";
import { MembersPage } from "./MembersPage";

const joined = "2026-01-01T00:00:00Z";
const expires = "2026-12-01T00:00:00Z";
const route = [{ path: "/w/:ws/members", element: <MembersPage /> }];
const cut = { "X-Truncated": "true" };
const member = (userId: string, displayName: string): Member => ({ userId, displayName, email: null, role: "member", joinedAt: joined });
const invitation = (id: string, email: string): Invitation => ({ id, email, role: "viewer", createdAt: joined, expiresAt: expires });
const more = () => screen.queryByRole("button", { name: "Load more" });
const pageReads = (calls: Call[], path: string) => calls.filter((c) => c.method === "GET" && c.path.startsWith(`${path}?`)).map((c) => c.path);
const membersOf = (answer: Parameters<typeof mockApi>[0][string], invitations: Parameters<typeof mockApi>[0][string] = { body: [] }) =>
  mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/members": answer, "GET /workspaces/w1/invitations": invitations });

describe("Load more on the members the API cut (F-060)", () => {
  it("offers no note and no button under a whole list", async () => {
    membersOf({ body: [member("u1", "Ada")] });
    renderAt("/w/w1/members", route);
    await screen.findByText("Ada");
    expect(screen.queryByText(/Only the first/)).not.toBeInTheDocument();
    expect(more()).not.toBeInTheDocument();
  });

  it("walks the cursor pages past the rows shown, adds the new ones once, and drops the button on the last page", async () => {
    // The plain list carries no cursor: the first cursor page holds the rows already shown, the second the new ones (one repeated).
    const calls = membersOf((call) => {
      if (call.path.includes("cursor=k1")) return { body: { items: [member("u2", "Bea"), member("u3", "Cem")], nextCursor: null } };
      if (call.path.includes("limit=")) return { body: { items: [member("u1", "Ada"), member("u2", "Bea")], nextCursor: "k1" } };
      return { body: [member("u1", "Ada"), member("u2", "Bea")], headers: cut };
    });
    renderAt("/w/w1/members", route);
    expect(await screen.findByText("Only the first 2 members are shown; the workspace has more.")).toBeInTheDocument();
    await userEvent.click(more()!);
    expect(await screen.findByText("Cem")).toBeInTheDocument();
    expect(screen.getAllByText("Bea")).toHaveLength(1);
    expect(screen.getAllByText("Ada")).toHaveLength(1);
    expect(more()).not.toBeInTheDocument();
    expect(screen.queryByText(/Only the first/)).not.toBeInTheDocument();
    expect(pageReads(calls, "/workspaces/w1/members")).toEqual(["/workspaces/w1/members?limit=100", "/workspaces/w1/members?limit=100&cursor=k1"]);
  });

  it("goes on from the cursor it reached, reading one page per press, while more are left", async () => {
    const calls = membersOf((call) => {
      if (call.path.includes("cursor=k1")) return { body: { items: [member("u3", "Cem")], nextCursor: null } };
      if (call.path.includes("limit=")) return { body: { items: [member("u2", "Bea")], nextCursor: "k1" } };
      return { body: [member("u1", "Ada")], headers: cut };
    });
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Bea")).toBeInTheDocument();
    expect(screen.getByText("Only the first 2 members are shown; the workspace has more.")).toBeInTheDocument();
    await userEvent.click(more()!);
    expect(await screen.findByText("Cem")).toBeInTheDocument();
    expect(more()).not.toBeInTheDocument();
    expect(pageReads(calls, "/workspaces/w1/members")).toEqual(["/workspaces/w1/members?limit=100", "/workspaces/w1/members?limit=100&cursor=k1"]);
  });

  it("shows the page's error when the next page fails, keeping the members shown and the button", async () => {
    membersOf((call) => (call.path.includes("limit=") ? { status: 500 } : { body: [member("u1", "Ada")], headers: cut }));
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByText("Ada")).toBeInTheDocument();
    expect(more()).toBeInTheDocument();
  });
});

describe("Load more on the open invitations the API cut (F-060)", () => {
  const openInvitations = async () => {
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("tab", { name: /Invitations/ }));
  };

  it("offers no button under a whole list of invitations", async () => {
    membersOf({ body: [member("u1", "Ada")] }, { body: [invitation("i1", "first+test@gmail.com")] });
    await openInvitations();
    await screen.findByText("first+test@gmail.com");
    expect(more()).not.toBeInTheDocument();
  });

  it("adds the next invitations once each and drops the button on the last page", async () => {
    const calls = membersOf({ body: [member("u1", "Ada")] }, (call) => (call.path.includes("limit=")
      ? { body: { items: [invitation("i1", "first+test@gmail.com"), invitation("i2", "second+test@gmail.com")], nextCursor: null } }
      : { body: [invitation("i1", "first+test@gmail.com")], headers: cut }));
    await openInvitations();
    expect(await screen.findByText("Only the first 1 open invitations are shown; there are more.")).toBeInTheDocument();
    await userEvent.click(more()!);
    expect(await screen.findByText("second+test@gmail.com")).toBeInTheDocument();
    expect(screen.getAllByText("first+test@gmail.com")).toHaveLength(1);
    expect(more()).not.toBeInTheDocument();
    expect(pageReads(calls, "/workspaces/w1/invitations")).toEqual(["/workspaces/w1/invitations?limit=100"]);
  });

  it("shows the page's error when the next invitations fail", async () => {
    membersOf({ body: [member("u1", "Ada")] }, (call) => (call.path.includes("limit=") ? { status: 500 } : { body: [invitation("i1", "first+test@gmail.com")], headers: cut }));
    await openInvitations();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByText("first+test@gmail.com")).toBeInTheDocument();
  });
});
