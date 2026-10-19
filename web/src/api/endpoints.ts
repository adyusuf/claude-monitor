import { request } from "./client";
import type {
  AgentActionKind, AgentActionView, AgentAsksView, AgentClaudeView, ClaudeAutoMode, ClaudePermissionMode, ApprovalKeyView, AgentRoleInput, AgentRoleView, AgentRow, AgentServicesView, AskView, AlertView, AuditRow, CommandRow, DeviceLookup, EventRow, GrantInput, Invitation, MachineDetail, MachineView, Me, Member, MetricSample, Page, PermissionRow, RemoteSettings, Role, RunOutputPage, ScheduleCreate, ScheduleView, SessionDetail, SessionRow, Settings, WebGrantView, WebJobView, WebRunView, WorkspaceInfo,
} from "./types";
import type {
  ApplicationInput, ApplicationSummary, ApplicationView, ComponentInput, ComponentView, ProjectInput, ProjectMemberView, ProjectRole,
  ProjectSummary, ProjectView, ReleaseInput, ReleaseView,
} from "./typesProjects";
import type {
  CodeLinkInput, CodeLinkView, IssueInput, IssueQuery, IssueRunView, IssueStatus, IssueSummary, IssueView, LinkKind, RunIssueView, TestCaseInput, TestCaseView,
  TestResult,
} from "./typesIssues";
import type { SprintInput, SprintQuery, SprintSummary, SprintView } from "./typesSprints";

const q = (params: Record<string, string | number | boolean | undefined | null>) => {
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
  /** `answers` (question text -> the option chosen) is only for the question tool's request, with an allow. */
  answer: (permissionId: string, decision: "allow" | "deny", reason?: string, answers?: Record<string, string>) =>
    request<void>("POST", `/permission-requests/${permissionId}/answer`, { decision, reason, answers }),

  agents: (id: string) => request<AgentRow[]>("GET", `/workspaces/${id}/agents`),
  revokeAgent: (agentId: string) => request<void>("POST", `/agents/${agentId}/revoke`),
  moveAgent: (agentId: string, workspaceId: string) => request<void>("PATCH", `/agents/${agentId}`, { workspaceId }),
  machines: (id: string) => request<MachineView[]>("GET", `/workspaces/${id}/machines`),
  agentRole: (agentId: string) => request<AgentRoleView>("GET", `/agents/${agentId}/role`),
  setAgentRole: (agentId: string, input: AgentRoleInput) => request<AgentRoleView>("PUT", `/agents/${agentId}/role`, input),
  machineDetail: (agentId: string) => request<MachineDetail>("GET", `/agents/${agentId}/detail`),
  setAgentTags: (agentId: string, tags: Record<string, string>) => request<{ agentId: string; tags: Record<string, string> }>("PUT", `/agents/${agentId}/tags`, { tags }),
  setAgentNote: (agentId: string, note: string | null) => request<{ agentId: string; note: string | null }>("PUT", `/agents/${agentId}/note`, { note }),
  setAgentServices: (agentId: string, share: boolean, code?: string) => request<AgentServicesView>("PUT", `/agents/${agentId}/services`, { share, code }),
  setAgentAsks: (agentId: string, accept: boolean, code?: string) => request<AgentAsksView>("PUT", `/agents/${agentId}/asks`, { accept, code }),
  agentActions: (agentId: string) => request<AgentActionView[]>("GET", `/agents/${agentId}/actions`),
  requestAgentAction: (agentId: string, kind: AgentActionKind, code?: string, extra?: { projectKey?: string; permissionMode?: ClaudePermissionMode; sessionRef?: string; prompt?: string }) =>
    request<AgentActionView>("POST", `/agents/${agentId}/actions`, { kind, code, ...extra }),
  startProjectTest: (agentId: string, projectKey: string) => request<{ id: string; status: string }>("POST", `/agents/${agentId}/test-runs`, { projectKey }),
  setAgentClaude: (agentId: string, accept: boolean, code?: string, autoMode?: ClaudeAutoMode) => request<AgentClaudeView>("PUT", `/agents/${agentId}/claude`, { accept, code, ...(autoMode ? { autoMode } : {}) }),
  agentAsks: (agentId: string) => request<AskView[]>("GET", `/agents/${agentId}/asks`),
  metrics: (agentId: string, minutes: number) => request<MetricSample[]>("GET", `/agents/${agentId}/metrics${q({ minutes })}`),
  alerts: (id: string, agent: string, includeResolved: boolean) =>
    request<AlertView[]>("GET", `/workspaces/${id}/alerts${q({ agent, resolved: includeResolved ? "true" : "false" })}`),
  runs: (id: string, opts: { agent?: string; before?: string | null; limit?: number }) =>
    request<WebRunView[]>("GET", `/workspaces/${id}/runs${q(opts)}`),
  run: (runId: string) => request<WebRunView>("GET", `/runs/${runId}`),
  runOutput: (runId: string, after: number, limit: number) => request<RunOutputPage>("GET", `/runs/${runId}/output${q({ after, limit })}`),
  approveRun: (runId: string, hash: string, code?: string, proof?: { keyId: string; signature: string }) =>
    request<void>("POST", `/runs/${runId}/approve`, { hash, code, keyId: proof?.keyId, signature: proof?.signature }),
  approvalKeys: () => request<ApprovalKeyView[]>("GET", "/me/approval-keys"),
  registerApprovalKey: (name: string, publicKey: string, code?: string) => request<ApprovalKeyView>("POST", "/me/approval-keys", { name, publicKey, code }),
  retireApprovalKey: (id: string) => request<void>("POST", `/me/approval-keys/${id}/retire`),
  denyRun: (runId: string, reason?: string) => request<void>("POST", `/runs/${runId}/deny`, { reason }),
  cancelRun: (runId: string) => request<void>("POST", `/runs/${runId}/cancel`),
  grants: (agentId: string) => request<WebGrantView[]>("GET", `/agents/${agentId}/grants`),
  createGrant: (agentId: string, input: GrantInput) => request<WebGrantView>("POST", `/agents/${agentId}/grants`, input),
  approveGrant: (grantId: string, code?: string) => request<void>("POST", `/grants/${grantId}/approve`, { code }),
  denyGrant: (grantId: string) => request<void>("POST", `/grants/${grantId}/deny`),
  revokeGrant: (grantId: string) => request<void>("POST", `/grants/${grantId}/revoke`),
  jobs: (agentId: string) => request<WebJobView[]>("GET", `/agents/${agentId}/jobs`),
  approveJob: (jobId: string, code?: string) => request<void>("POST", `/jobs/${jobId}/approve`, { code }),
  denyJob: (jobId: string) => request<void>("POST", `/jobs/${jobId}/deny`),
  retireJob: (jobId: string) => request<void>("POST", `/jobs/${jobId}/retire`),
  schedules: (agentId: string) => request<ScheduleView[]>("GET", `/agents/${agentId}/schedules`),
  createSchedule: (body: ScheduleCreate) => request<ScheduleView>("POST", "/schedules", body),
  pauseSchedule: (id: string) => request<void>("POST", `/schedules/${id}/pause`),
  resumeSchedule: (id: string, code?: string) => request<void>("POST", `/schedules/${id}/resume`, { code }),
  endSchedule: (id: string) => request<void>("POST", `/schedules/${id}/end`),
  remoteSettings: (id: string) => request<RemoteSettings>("GET", `/workspaces/${id}/remote-settings`),
  saveRemoteSettings: (id: string, settings: RemoteSettings) => request<RemoteSettings>("PUT", `/workspaces/${id}/remote-settings`, settings),
  projects: (ws: string, opts: { q?: string; status?: string; cursor?: string | null; limit?: number }) =>
    request<Page<ProjectSummary>>("GET", `/workspaces/${ws}/projects${q(opts)}`),
  createProject: (ws: string, input: ProjectInput) => request<ProjectView>("POST", `/workspaces/${ws}/projects`, input),
  project: (id: string) => request<ProjectView>("GET", `/projects/${id}`),
  updateProject: (id: string, input: ProjectInput) => request<void>("PUT", `/projects/${id}`, input),
  archiveProject: (id: string) => request<void>("DELETE", `/projects/${id}`),
  restoreProject: (id: string) => request<void>("POST", `/projects/${id}/restore`),
  projectMembers: (id: string) => request<ProjectMemberView[]>("GET", `/projects/${id}/members`),
  putProjectMember: (id: string, userId: string, role: ProjectRole) => request<void>("PUT", `/projects/${id}/members/${userId}`, { role }),
  removeProjectMember: (id: string, userId: string) => request<void>("DELETE", `/projects/${id}/members/${userId}`),
  applications: (projectId: string, opts: { status?: string; cursor?: string | null; limit?: number }) =>
    request<Page<ApplicationSummary>>("GET", `/projects/${projectId}/applications${q(opts)}`),
  createApplication: (projectId: string, input: ApplicationInput) => request<ApplicationView>("POST", `/projects/${projectId}/applications`, input),
  application: (id: string) => request<ApplicationView>("GET", `/applications/${id}`),
  updateApplication: (id: string, input: ApplicationInput) => request<void>("PUT", `/applications/${id}`, input),
  archiveApplication: (id: string) => request<void>("DELETE", `/applications/${id}`),
  restoreApplication: (id: string) => request<void>("POST", `/applications/${id}/restore`),
  components: (applicationId: string) => request<ComponentView[]>("GET", `/applications/${applicationId}/components`),
  addComponent: (applicationId: string, input: ComponentInput) => request<ComponentView>("POST", `/applications/${applicationId}/components`, input),
  putComponent: (id: string, input: ComponentInput) => request<void>("PUT", `/components/${id}`, input),
  archiveComponent: (id: string) => request<void>("DELETE", `/components/${id}`),
  releases: (applicationId: string) => request<ReleaseView[]>("GET", `/applications/${applicationId}/releases`),
  addRelease: (applicationId: string, input: ReleaseInput) => request<ReleaseView>("POST", `/applications/${applicationId}/releases`, input),
  putRelease: (id: string, input: ReleaseInput) => request<void>("PUT", `/releases/${id}`, input),
  cancelRelease: (id: string) => request<void>("DELETE", `/releases/${id}`),
  issues: (projectId: string, opts: IssueQuery) => request<Page<IssueSummary>>("GET", `/projects/${projectId}/issues${q(opts)}`),
  createIssue: (projectId: string, input: IssueInput) => request<IssueView>("POST", `/projects/${projectId}/issues`, input),
  issue: (id: string) => request<IssueView>("GET", `/issues/${id}`),
  updateIssue: (id: string, input: IssueInput) => request<void>("PUT", `/issues/${id}`, input),
  archiveIssue: (id: string) => request<void>("DELETE", `/issues/${id}`),
  restoreIssue: (id: string) => request<void>("POST", `/issues/${id}/restore`),
  moveIssue: (id: string, afterIssueId: string | null) => request<void>("PUT", `/issues/${id}/position`, { afterIssueId }),
  addIssueLink: (id: string, otherIssueId: string, kind: LinkKind) => request<void>("POST", `/issues/${id}/links`, { otherIssueId, kind }),
  removeIssueLink: (linkId: string) => request<void>("DELETE", `/issue-links/${linkId}`),
  testCases: (issueId: string) => request<TestCaseView[]>("GET", `/issues/${issueId}/test-cases`),
  addTestCase: (issueId: string, input: TestCaseInput) => request<TestCaseView>("POST", `/issues/${issueId}/test-cases`, input),
  putTestCase: (id: string, input: TestCaseInput) => request<void>("PUT", `/test-cases/${id}`, input),
  deleteTestCase: (id: string) => request<void>("DELETE", `/test-cases/${id}`),
  setTestResult: (id: string, result: TestResult, note: string | null) => request<void>("POST", `/test-cases/${id}/result`, { result, note }),
  codeLinks: (issueId: string) => request<CodeLinkView[]>("GET", `/issues/${issueId}/code-links`),
  addCodeLink: (issueId: string, input: CodeLinkInput) => request<CodeLinkView>("POST", `/issues/${issueId}/code-links`, input),
  deleteCodeLink: (id: string) => request<void>("DELETE", `/code-links/${id}`),
  issueRuns: (issueId: string) => request<IssueRunView[]>("GET", `/issues/${issueId}/runs`),
  linkIssueRun: (issueId: string, runId: string) => request<void>("POST", `/issues/${issueId}/runs`, { runId }),
  unlinkIssueRun: (issueId: string, runId: string) => request<void>("DELETE", `/issues/${issueId}/runs/${runId}`),
  runIssues: (runId: string) => request<RunIssueView[]>("GET", `/runs/${runId}/issues`),
  sprints: (projectId: string, opts: SprintQuery) => request<Page<SprintSummary>>("GET", `/projects/${projectId}/sprints${q(opts)}`),
  createSprint: (projectId: string, input: SprintInput) => request<SprintView>("POST", `/projects/${projectId}/sprints`, input),
  sprint: (id: string) => request<SprintView>("GET", `/sprints/${id}`),
  updateSprint: (id: string, input: SprintInput) => request<void>("PUT", `/sprints/${id}`, input),
  deleteSprint: (id: string) => request<void>("DELETE", `/sprints/${id}`),
  startSprint: (id: string) => request<void>("POST", `/sprints/${id}/start`),
  /** moveUnfinishedTo: a planned sprint of the same scope, or null for the backlog. */
  closeSprint: (id: string, moveUnfinishedTo: string | null) => request<void>("POST", `/sprints/${id}/close`, { moveUnfinishedTo }),
  addSprintIssues: (id: string, issueIds: string[]) => request<void>("POST", `/sprints/${id}/issues`, { issueIds }),
  removeSprintIssue: (id: string, issueId: string) => request<void>("DELETE", `/sprints/${id}/issues/${issueId}`),
  setIssueStatus: (id: string, status: IssueStatus) => request<void>("PUT", `/issues/${id}/status`, { status }),
  lookupDevice: (code: string) => request<DeviceLookup>("GET", `/device/lookup/${encodeURIComponent(code)}`),
  approveDevice: (userCode: string, workspaceId: string) => request<void>("POST", "/device/approve", { userCode, workspaceId }),
  denyDevice: (userCode: string, workspaceId: string) => request<void>("POST", "/device/deny", { userCode, workspaceId }),
};

/** The address a page links to for signing in or linking with a provider (a full page navigation, not fetch). */
export const providerUrl = (provider: string, mode: "signin" | "link") => `/api/auth/external/${provider}?mode=${mode}`;
export const exportUrl = "/api/me/export";
export const streamUrl = (workspaceId: string) => `/api/workspaces/${workspaceId}/stream`;
