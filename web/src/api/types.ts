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
