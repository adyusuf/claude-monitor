// The API's response shapes (src/Podium.Api/Endpoints). Type-only.

export type Role = "owner" | "admin" | "member" | "viewer";
export type SessionStatus = "active" | "idle" | "waiting" | "ended";

export interface MeWorkspace { id: string; name: string; role: Role }
export interface Me {
  id: string;
  email: string | null;
  displayName: string;
  hasPassword: boolean;
  providers: string[];
  workspaces: MeWorkspace[];
  mfaEnabled?: boolean;
  /** The language kept on the account (ADR-0014 Part A): the mails to the person follow it. */
  language?: string | null;
  /** May change the server-wide settings (ADR-0017); an older server leaves it out, which means no. */
  serverAdmin?: boolean;
}

export type AgentUpdateMode = "off" | "check" | "on";
export interface Settings { maskSecrets: boolean; retentionDays: number; eventMaxBytes: number; agentUpdate: AgentUpdateMode; claudeUpdate?: boolean; enforceAccessPolicies?: boolean }
export interface WorkspaceInfo { id: string; name: string; role: Role; settings: Settings }
export interface Member { userId: string; displayName: string; email: string | null; role: Role; joinedAt: string }
export interface Invitation { id: string; email: string; role: Role; createdAt: string; expiresAt: string }

export interface Page<T> { items: T[]; next: string | null }

export interface SessionRow {
  id: string;
  harnessKind: string;
  title: string | null;
  model: string | null;
  gitBranch: string | null;
  status: SessionStatus;
  startedAt: string;
  lastEventAt: string;
  endedAt: string | null;
  projectId: string | null;
  projectName: string | null;
  agentId: string;
  hostname: string;
  ownerId: string;
  ownerName: string;
  costUsd: number | null;
  openPermissions: number;
  /** The push transport the session's machine has on ("inbox" or "channel"); absent or null when it is off or the agent is older. */
  pushTransport?: string | null;
  /** Claude Code started this session in the background from the web (ADR-0014); absent on an older API. */
  startedFromWeb?: boolean;
  /** The short id of that start, for a stop from the web; null when the web did not start it. */
  webSessionRef?: string | null;
  /** The session's repository key, which names its access policy (ADR-0020 §4); absent on an older API. */
  projectKey?: string | null;
}
export interface TaskRow { id: string; externalId: string; subject: string; status: string; updatedAt: string }
export interface SubagentRow { id: string; agentType: string; description: string | null; status: string; startedAt: string; endedAt: string | null }
export interface UsageRow {
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  costUsd: number | null;
}
export interface SessionDetail { session: SessionRow; canCommand: boolean; tasks: TaskRow[]; subagents: SubagentRow[]; usage: UsageRow[] }
export interface EventRow { id: number; kind: string; occurredAt: string; truncated: boolean; payload: Record<string, unknown> }

export interface CommandRow {
  id: string;
  kind: "prompt" | "stop";
  body: string | null;
  status: string;
  createdBy: string;
  createdAt: string;
  expiresAt: string;
  deliveredAt: string | null;
  appliedAt: string | null;
  result: string | null;
  /** How an applied command reached the session; absent or null on an older API or an older agent. */
  transport?: "hook" | "inbox" | "channel" | string | null;
  /** Claude's first message with text after the command was applied; absent on an older API. */
  replyEventId?: number | null;
  /** The start of that message; replyMore says the message goes on. */
  replyText?: string | null;
  replyMore?: boolean;
}
export interface PermissionRow {
  id: string;
  toolName: string;
  toolInput: Record<string, unknown>;
  status: "open" | "answered" | "expired";
  createdAt: string;
  expiresAt: string;
  decision: "allow" | "deny" | null;
  reason: string | null;
  answeredAt: string | null;
}

/** How many of something are in one state. */
export interface StateCount { state: string; count: number }
/** The dashboard's counts (runs and permission answers over the last `days` days). */
export interface DashboardView {
  days: number;
  projects: StateCount[];
  sprints: StateCount[];
  runs: StateCount[];
  /** Permission requests by outcome: allow, auto_allowed, deny, expired, open. */
  approvals: StateCount[];
  openAlerts: number;
}

/** How long an allow stands for the same tool on the same machine (ADR-0019). */
export type ApprovalDuration = "once" | "5m" | "10m" | "30m" | "1h" | "always";
export const APPROVAL_DURATIONS: readonly ApprovalDuration[] = ["once", "5m", "10m", "30m", "1h", "always"];

/** A permission request on one of the viewer's own sessions, with where it comes from. */
export interface ApprovalRequestRow {
  id: string;
  sessionId: string;
  sessionTitle: string | null;
  projectName: string | null;
  agentId: string;
  hostname: string;
  toolName: string;
  toolInput: Record<string, unknown>;
  status: "open" | "answered" | "expired";
  createdAt: string;
  expiresAt: string;
  decision: "allow" | "deny" | null;
  answeredAt: string | null;
  answeredByName: string | null;
  answeredByRule: string | null;
  /** Whether an allow may stand for a while (not on a strict machine, not a question or a plan, not a too long command). */
  canRemember: boolean;
  strict: boolean;
}

/** One of the viewer's standing approvals. */
export interface ApprovalRuleRow {
  id: string;
  agentId: string;
  hostname: string;
  toolName: string;
  command: string | null;
  duration: Exclude<ApprovalDuration, "once">;
  createdAt: string;
  expiresAt: string | null;
  useCount: number;
  lastUsedAt: string | null;
  /** The project the rule acts in (null: sessions with no project). */
  projectName?: string | null;
}

export interface AgentRow {
  id: string;
  machineId: string;
  hostname: string;
  os: string;
  arch: string;
  version: string;
  status: "active" | "revoked";
  userId: string;
  userName: string;
  enrolledAt: string;
  lastHeartbeatAt: string | null;
  revokedAt: string | null;
  /** The newest build this server hands out for the machine's OS and CPU, and whether it is newer than the running one (decided by the API). */
  latestVersion?: string | null;
  updateAvailable?: boolean
}
export interface DeviceLookup { userCode: string; hostname: string; os: string; arch: string; agentVersion: string; createdAt: string; expiresAt: string }
export interface AuditRow { id: number; action: string; actorUserId: string | null; actorAgentId: string | null; targetType: string | null; targetId: string | null; at: string; detail: unknown }

// Remote work (ADR-0005): machines, metrics, alerts, runs, grants, jobs.
export * from "./typesMachines";
export * from "./typesRemote";
