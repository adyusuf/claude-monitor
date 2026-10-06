// The API's response shapes (src/ClaudeMonitor.Api/Endpoints). Type-only.

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
}

export interface Settings { maskSecrets: boolean; retentionDays: number; eventMaxBytes: number }
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
}
export interface DeviceLookup { userCode: string; hostname: string; os: string; arch: string; agentVersion: string; createdAt: string; expiresAt: string }
export interface AuditRow { id: number; action: string; actorUserId: string | null; actorAgentId: string | null; targetType: string | null; targetId: string | null; at: string; detail: unknown }

// Remote work (ADR-0004): machines, metrics, alerts, runs, grants, jobs.
export type ExecLevel = "off" | "argv" | "shell";
export interface DiskSample { mount: string; usedBytes: number; totalBytes: number }
export interface MetricSample { sampledAt: string; cpuPct: number; memUsedBytes: number; memTotalBytes: number; disks: DiskSample[] }
export interface MachineView {
  agentId: string;
  hostname: string;
  os: string;
  userName: string;
  execLevel: ExecLevel;
  serviceMode: boolean;
  online: boolean;
  lastSeenAt: string | null;
  latest: MetricSample | null;
  openAlerts: number;
}
export type AlertKind = "cpu" | "memory" | "disk" | "offline";
export interface AlertView {
  id: string;
  agentId: string;
  hostname: string;
  kind: AlertKind;
  subject: string;
  state: "open" | "resolved";
  thresholdPct: number;
  lastValue: number;
  peakValue: number;
  openedAt: string;
  resolvedAt: string | null;
}

export type RunStatus =
  | "pending_approval" | "approved" | "delivered" | "running" | "succeeded" | "failed" | "timed_out" | "denied" | "expired" | "cancelled";
export interface WebRunView {
  id: string;
  targetAgentId: string;
  targetHostname: string;
  targetUser: string;
  requesterAgentId: string;
  requesterHostname: string;
  requesterUser: string;
  requesterSessionId: string | null;
  visible: boolean;
  mode: "argv" | "shell";
  argv: string[] | null;
  shellCommand: string | null;
  cwd: string | null;
  timeoutSeconds: number;
  reason: string | null;
  status: RunStatus;
  exitCode: number | null;
  error: string | null;
  outputBytes: number;
  outputTruncated: boolean;
  createdAt: string;
  expiresAt: string;
  endedAt: string | null;
  grantId: string | null;
  jobId: string | null;
  canDecide: boolean;
  selfApproval: boolean;
  hash: string | null;
  interpreter: boolean;
  recentOutput: boolean;
}
export interface RunOutputChunk { seq: number; stream: string; body: string; gapBefore: boolean }
export interface RunOutputPage { chunks: RunOutputChunk[]; nextSeq: number; done: boolean }

export type GrantStatus = "requested" | "active" | "denied" | "revoked" | "expired";
export interface WebGrantView {
  id: string;
  targetAgentId: string;
  ownerUserId: string;
  granteeUserId: string;
  granteeName: string;
  granteeAgentId: string | null;
  requestedByAgentId: string | null;
  requestedByHostname: string | null;
  template: string[];
  cwd: string;
  maxTimeoutSeconds: number;
  reason: string | null;
  status: GrantStatus;
  createdAt: string;
  expiresAt: string;
  decidedAt: string | null;
  revokedAt: string | null;
  useCount: number;
  lastUsedAt: string | null;
  canDecide: boolean;
  canRevoke: boolean;
}
export interface GrantInput { template: string[]; cwd: string; maxTimeoutSeconds: number; days: number; reason?: string; code?: string }

export type JobStatus = "proposed" | "active" | "denied" | "retired";
export interface WebJobView {
  id: string;
  targetAgentId: string;
  ownerUserId: string;
  name: string;
  argv: string[];
  cwd: string;
  timeoutSeconds: number;
  reason: string | null;
  status: JobStatus;
  proposedByUserId: string;
  proposedByName: string;
  proposedByAgentId: string | null;
  proposedByHostname: string | null;
  createdAt: string;
  decidedAt: string | null;
  retiredAt: string | null;
  canDecide: boolean;
  canRetire: boolean;
}

export interface RemoteSettings { remoteRunsEnabled: boolean; alertCpuPct: number; alertMemoryPct: number; alertDiskPct: number; alertSustainSeconds: number }
