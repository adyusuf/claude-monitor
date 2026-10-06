import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { AgentRow } from "../api/types";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AcceptInvitationPage, AccountPage, DownloadPage } from "./AccountPages";
import { DevicePage, MachinesPage } from "./MachinesPage";
import { MembersPage } from "./MembersPage";
import { NewWorkspacePage, WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const agent = (over: Partial<AgentRow> = {}): AgentRow => ({
  id: "a1", machineId: "m1", hostname: "laptop-1", os: "macos", arch: "arm64", version: "0.3.0", status: "active",
  userId: "u2", userName: "Diğer Kişi", enrolledAt: new Date().toISOString(), lastHeartbeatAt: null, revokedAt: null, ...over,
});

describe("machines and devices", () => {
  it("lists machines and lets an owner disconnect one", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent(), agent({ id: "a2", status: "revoked" })] },
      "POST /agents/a1/revoke": { status: 204 },
    });
    renderAt("/w/w1/machines", [{ path: "/w/:ws/machines", element: <MachinesPage /> }]);
    expect(await screen.findAllByText("laptop-1")).toHaveLength(2);
    expect(screen.getByText("Disconnected")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(calls.some((c) => c.path === "/agents/a1/revoke")).toBe(true));
  });

  it("marks a machine whose agent can be updated, and only an active one", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [
        agent({ updateAvailable: true, latestVersion: "0.3.1" }),
        agent({ id: "a2", hostname: "desk", updateAvailable: false, latestVersion: "0.3.0" }),
        agent({ id: "a3", hostname: "old", status: "revoked", updateAvailable: true, latestVersion: "0.3.1" }),
        agent({ id: "a4", hostname: "legacy" }), // an API that predates the field
      ] },
    });
    renderAt("/w/w1/machines", [{ path: "/w/:ws/machines", element: <MachinesPage /> }]);
    expect(await screen.findAllByText("Update available: 0.3.1")).toHaveLength(1);
  });

  it("shows an empty workspace and a failed load", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [] } });
    renderAt("/w/w1/machines", [{ path: "/w/:ws/machines", element: <MachinesPage /> }]);
    expect(await screen.findByText("No machine is connected yet.")).toBeInTheDocument();
  });

  it("approves the code the agent showed, for a chosen workspace", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /device/lookup/BCDF-GHJK": { body: { userCode: "BCDF-GHJK", hostname: "desk", os: "windows", arch: "x64", agentVersion: "0.3.0", createdAt: "", expiresAt: "" } },
      "POST /device/approve": { status: 204 },
    });
    renderAt("/device?code=BCDF-GHJK", [{ path: "/device", element: <DevicePage /> }]);
    expect(await screen.findByText("desk · windows x64 · agent 0.3.0")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Connect this machine" }));
    expect(await screen.findByText(/The machine is connected/)).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/device/approve")?.body).toEqual({ userCode: "BCDF-GHJK", workspaceId: "w1" });
  });

  it("refuses a request and reports an unknown code", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /device/lookup/ZZZZ-ZZZZ": { status: 404 },
      "GET /device/lookup/BCDF-GHJK": { body: { userCode: "BCDF-GHJK", hostname: "desk", os: "macos", arch: "arm64", agentVersion: "0.3.0", createdAt: "", expiresAt: "" } },
      "POST /device/deny": { status: 204 },
    });
    renderAt("/device", [{ path: "/device", element: <DevicePage /> }]);
    await userEvent.type(await screen.findByLabelText("Code shown by the agent"), "zzzz-zzzz");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("Not found.")).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("Code shown by the agent"));
    await userEvent.type(screen.getByLabelText("Code shown by the agent"), "bcdf-ghjk");
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));
    await userEvent.click(await screen.findByRole("button", { name: "This is not me" }));
    expect(await screen.findByText("The request was refused.")).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/device/deny")?.body).toEqual({ userCode: "BCDF-GHJK", workspaceId: "w1" });
  });

  it("sends the workspace the user picked when refusing", async () => {
    const me = { ...ME, workspaces: [...ME.workspaces, { id: "w3", name: "Third", role: "member" as const }] };
    const calls = mockApi({
      "GET /me": { body: me },
      "GET /device/lookup/BCDF-GHJK": { body: { userCode: "BCDF-GHJK", hostname: "desk", os: "macos", arch: "arm64", agentVersion: "0.3.0", createdAt: "", expiresAt: "" } },
      "POST /device/deny": { status: 204 },
    });
    renderAt("/device?code=BCDF-GHJK", [{ path: "/device", element: <DevicePage /> }]);
    await userEvent.selectOptions(await screen.findByLabelText("Report to workspace"), "w3");
    await userEvent.click(screen.getByRole("button", { name: "This is not me" }));
    expect(await screen.findByText("The request was refused.")).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/device/deny")?.body).toEqual({ userCode: "BCDF-GHJK", workspaceId: "w3" });
  });

  it("cannot refuse or approve when the user may contribute to no workspace", async () => {
    const calls = mockApi({
      "GET /me": { body: { ...ME, workspaces: [{ id: "w2", name: "Other", role: "viewer" }] } },
      "GET /device/lookup/BCDF-GHJK": { body: { userCode: "BCDF-GHJK", hostname: "desk", os: "macos", arch: "arm64", agentVersion: "0.3.0", createdAt: "", expiresAt: "" } },
      "POST /device/deny": { status: 204 },
    });
    renderAt("/device?code=BCDF-GHJK", [{ path: "/device", element: <DevicePage /> }]);
    const deny = await screen.findByRole("button", { name: "This is not me" });
    expect(deny).toBeDisabled();
    expect(screen.getByRole("button", { name: "Connect this machine" })).toBeDisabled();
    await userEvent.click(deny);
    expect(calls.some((c) => c.path === "/device/deny")).toBe(false);
  });
});

describe("members", () => {
  it("invites, changes a role, removes and revokes an invitation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/members": { body: [
        { userId: "u1", displayName: "Örnek Kişi", email: "a@b.co", role: "owner", joinedAt: new Date().toISOString() },
        { userId: "u2", displayName: "Diğer Kişi", email: "c@d.co", role: "member", joinedAt: new Date().toISOString() },
      ] },
      "GET /workspaces/w1/invitations": { body: [{ id: "i1", email: "new+x@gmail.com", role: "viewer", createdAt: "", expiresAt: new Date().toISOString() }] },
      "POST /workspaces/w1/invitations": { status: 201, body: { id: "i2" } },
      "PATCH /workspaces/w1/members/u2": { status: 204 },
      "DELETE /workspaces/w1/members/u2": { status: 204 },
      "DELETE /workspaces/w1/invitations/i1": { status: 204 },
    });
    renderAt("/w/w1/members", [{ path: "/w/:ws/members", element: <MembersPage /> }]);
    expect(await screen.findByText("Diğer Kişi")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("E-mail to invite"), "someone+y@gmail.com");
    await userEvent.click(screen.getByRole("button", { name: "Invite" }));
    expect(await screen.findByText("Invitation sent.")).toBeInTheDocument();
    await userEvent.selectOptions(within(screen.getByRole("table")).getByLabelText("Role"), "admin");
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/workspaces/w1/invitations/i1")).toBe(true));
    expect(calls.find((c) => c.method === "PATCH")?.body).toEqual({ role: "admin" });
    expect(calls.some((c) => c.method === "DELETE" && c.path === "/workspaces/w1/members/u2")).toBe(true);
  });

  it("lets a viewer only read and leave", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w2/members": { body: [{ userId: "u1", displayName: "Örnek Kişi", email: null, role: "viewer", joinedAt: "" }] },
      "DELETE /workspaces/w2/members/u1": { status: 204 },
    });
    renderAt("/w/w2/members", [{ path: "/w/:ws/members", element: <MembersPage /> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Leave workspace" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE")).toBe(true));
    expect(screen.queryByText("Open invitations")).not.toBeInTheDocument();
  });
});

describe("workspace settings and account", () => {
  it("saves the name and capture settings and shows the audit log", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1": { body: { id: "w1", name: "Team", role: "owner", settings: { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 } } },
      "GET /workspaces/w1/audit": { body: { items: [{ id: 1, action: "workspace.created", actorUserId: "u1", actorAgentId: null, targetType: null, targetId: null, at: new Date().toISOString(), detail: null }], next: null } },
      "PATCH /workspaces/w1": { status: 204 },
      "PUT /workspaces/w1/settings": { status: 204 },
    });
    renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
    expect(await screen.findByText("workspace.created")).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("Name"));
    await userEvent.type(screen.getByLabelText("Name"), "Renamed");
    await userEvent.click(screen.getByRole("checkbox", { name: /Mask secrets before upload/ }));
    await userEvent.clear(screen.getByLabelText("Keep events for (days)"));
    await userEvent.type(screen.getByLabelText("Keep events for (days)"), "30");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Saved.")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ maskSecrets: false, retentionDays: 30, eventMaxBytes: 262144, agentUpdate: "off", claudeUpdate: false });
    expect(calls.find((c) => c.method === "PATCH")?.body).toEqual({ name: "Renamed" });
  });

  it("lets an admin choose how far agents may update themselves, off by default", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1": { body: { id: "w1", name: "Team", role: "owner", settings: { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144, agentUpdate: "check" } } },
      "GET /workspaces/w1/audit": { body: { items: [], next: null } },
      "PUT /workspaces/w1/settings": { status: 204 },
    });
    renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
    const select = await screen.findByLabelText("Agent updates");
    expect(select).toHaveValue("check");
    await userEvent.selectOptions(select, "on");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Saved.")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ agentUpdate: "on" });
  });

  it("lets an admin allow Claude Code updates, off by default", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1": { body: { id: "w1", name: "Team", role: "owner", settings: { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144, agentUpdate: "off" } } },
      "GET /workspaces/w1/audit": { body: { items: [], next: null } },
      "PUT /workspaces/w1/settings": { status: 204 },
    });
    renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
    const box = await screen.findByLabelText(/Allow agents to update Claude Code/);
    expect(box).not.toBeChecked(); // an API that predates the field reads as off
    await userEvent.click(box);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Saved.")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ claudeUpdate: true });
  });

  it("shows agent updates as off when the API predates the setting", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1": { body: { id: "w1", name: "Team", role: "owner", settings: { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 } } },
      "GET /workspaces/w1/audit": { body: { items: [], next: null } },
    });
    renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
    expect(await screen.findByLabelText("Agent updates")).toHaveValue("off");
  });

  it("creates a workspace", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "POST /workspaces": { status: 201, body: { id: "w9" } } });
    renderAt("/workspaces/new", [{ path: "/workspaces/new", element: <NewWorkspacePage /> }, { path: "/w/:ws/sessions", element: <div>new sessions</div> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Create" }));
    expect(screen.getByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Name"), "Second");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    expect(await screen.findByText("new sessions")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ name: "Second" });
  });

  it("shows the sign-in methods with links for the unlinked ones", async () => {
    mockApi({ "GET /me": { body: { ...ME, providers: ["github"] } } });
    renderAt("/account?error=provider_in_use", [{ path: "/account", element: <AccountPage /> }]);
    expect(await screen.findByText("Linked")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Link Google" })).toHaveAttribute("href", "/api/auth/external/google?mode=link");
    expect(screen.getByText("That provider account is linked to another user.")).toBeInTheDocument();
  });

  it("accepts an invitation and reports a spent one", async () => {
    mockApi({ "GET /me": { body: ME }, "POST /invitations/accept": { status: 400, body: { errors: { token: ["invitation_other_address"] } } } });
    renderAt("/invitations/accept?token=t", [{ path: "/invitations/accept", element: <AcceptInvitationPage /> }]);
    expect(await screen.findByText(/This invitation was sent to another address/)).toBeInTheDocument();
  });

  it("gives the download page this site's own address in the login command", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/download", [{ path: "/download", element: <DownloadPage /> }]);
    expect(await screen.findByText(`cm-agent login --server ${window.location.origin}`)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "macOS · Apple silicon" })).toHaveAttribute("href", "/downloads/cm-agent-macos-arm64.zip");
  });
});
