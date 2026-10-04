import { request } from "./client";
import type {
  AgentRow, AuditRow, CommandRow, DeviceLookup, EventRow, Invitation, Me, Member, Page, PermissionRow, Role,
  SessionDetail, SessionRow, Settings, WorkspaceInfo,
} from "./types";

const q = (params: Record<string, string | number | undefined | null>) => {
  const s = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") s.set(k, String(v));
  const text = s.toString();
  return text ? `?${text}` : "";
};

/** Every API call the web makes, one function each. */
export const api = {
  me: () => request<Me>("GET", "/me"),
  providers: () => request<{ available: string[] }>("GET", "/auth/providers"),
  register: (email: string, password: string, displayName: string) =>
    request<void>("POST", "/auth/register", { email, password, displayName }),
  verifyEmail: (token: string) => request<void>("POST", "/auth/verify-email", { token }),
  login: (email: string, password: string) => request<void>("POST", "/auth/login", { email, password }),
  logout: () => request<void>("POST", "/auth/logout"),
  deleteAccount: (password: string | undefined, confirm: string, code?: string) => request<void>("POST", "/me/delete", { password, confirm, code }),
  /** Without a token the API takes the provider sign-in's pending token from its own cookie. */
  mfaSignIn: (token: string | undefined, code: string) => request<void>("POST", "/auth/mfa", { token, code }),
  mfaSetup: () => request<{ secret: string; uri: string }>("POST", "/me/mfa/setup"),
  mfaEnable: (code: string) => request<{ recoveryCodes: string[] }>("POST", "/me/mfa/enable", { code }),
  mfaDisable: (code: string) => request<void>("POST", "/me/mfa/disable", { code }),
  forgot: (email: string) => request<void>("POST", "/auth/password/forgot", { email }),
  reset: (token: string, password: string) => request<void>("POST", "/auth/password/reset", { token, password }),

  createWorkspace: (name: string) => request<{ id: string }>("POST", "/workspaces", { name }),
  workspace: (id: string) => request<WorkspaceInfo>("GET", `/workspaces/${id}`),
  renameWorkspace: (id: string, name: string) => request<void>("PATCH", `/workspaces/${id}`, { name }),
  saveSettings: (id: string, settings: Partial<Settings>) => request<void>("PUT", `/workspaces/${id}/settings`, settings),
  members: (id: string) => request<Member[]>("GET", `/workspaces/${id}/members`),
  changeRole: (id: string, userId: string, role: Role) => request<void>("PATCH", `/workspaces/${id}/members/${userId}`, { role }),
  removeMember: (id: string, userId: string) => request<void>("DELETE", `/workspaces/${id}/members/${userId}`),
  invitations: (id: string) => request<Invitation[]>("GET", `/workspaces/${id}/invitations`),
  invite: (id: string, email: string, role: Role) => request<{ id: string }>("POST", `/workspaces/${id}/invitations`, { email, role }),
  revokeInvitation: (id: string, invitationId: string) => request<void>("DELETE", `/workspaces/${id}/invitations/${invitationId}`),
  acceptInvitation: (token: string) => request<{ workspaceId: string }>("POST", "/invitations/accept", { token }),
  audit: (id: string, before?: string | null) => request<Page<AuditRow>>("GET", `/workspaces/${id}/audit${q({ before })}`),

  sessions: (id: string, opts: { q?: string; status?: string; cursor?: string | null; limit?: number }) =>
    request<Page<SessionRow>>("GET", `/workspaces/${id}/sessions${q(opts)}`),
  session: (id: string) => request<SessionDetail>("GET", `/sessions/${id}`),
  events: (id: string, opts: { before?: string | null; kind?: string; limit?: number }) =>
    request<Page<EventRow>>("GET", `/sessions/${id}/events${q(opts)}`),
  commands: (id: string) => request<CommandRow[]>("GET", `/sessions/${id}/commands`),
  sendCommand: (id: string, kind: "prompt" | "stop", body?: string) => request<{ id: string }>("POST", `/sessions/${id}/commands`, { kind, body }),
  cancelCommand: (commandId: string) => request<void>("POST", `/commands/${commandId}/cancel`),
  permissions: (id: string, status?: string) => request<PermissionRow[]>("GET", `/sessions/${id}/permission-requests${q({ status })}`),
  answer: (permissionId: string, decision: "allow" | "deny", reason?: string) =>
    request<void>("POST", `/permission-requests/${permissionId}/answer`, { decision, reason }),

  agents: (id: string) => request<AgentRow[]>("GET", `/workspaces/${id}/agents`),
  revokeAgent: (agentId: string) => request<void>("POST", `/agents/${agentId}/revoke`),
  moveAgent: (agentId: string, workspaceId: string) => request<void>("PATCH", `/agents/${agentId}`, { workspaceId }),
  lookupDevice: (code: string) => request<DeviceLookup>("GET", `/device/lookup/${encodeURIComponent(code)}`),
  approveDevice: (userCode: string, workspaceId: string) => request<void>("POST", "/device/approve", { userCode, workspaceId }),
  denyDevice: (userCode: string, workspaceId: string) => request<void>("POST", "/device/deny", { userCode, workspaceId }),
};

/** The address a page links to for signing in or linking with a provider (a full page navigation, not fetch). */
export const providerUrl = (provider: string, mode: "signin" | "link") => `/api/auth/external/${provider}?mode=${mode}`;
export const exportUrl = "/api/me/export";
export const streamUrl = (workspaceId: string) => `/api/workspaces/${workspaceId}/stream`;
