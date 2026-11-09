// Remote work (ADR-0005, ADR-0010): runs and their output, grants, jobs, the remote settings, schedules and approval keys.
// Type-only; re-exported from types.ts.

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
  mode: "argv" | "shell" | "script";
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
  // ADR-0008: the project and kind a run named, a script run's text and the role the card shows, and what its output account says.
  // Optional: an older API omits them.
  projectKey?: string | null;
  kind?: string | null;
  scriptInterpreter?: string | null;
  scriptBody?: string | null;
  scriptArgs?: string[] | null;
  /** ADR-0010: the text files a script run carries next to the script, and the text for its stdin. Absent on an older API. */
  scriptFiles?: { path: string; text: string }[] | null;
  scriptStdin?: string | null;
  /** ADR-0010 §4: the target runs nothing that a pinned owner key did not sign; the card then needs this browser's key. */
  approvalsRequired?: boolean;
  approvalKeyId?: string | null;
  interpreterPath?: string | null;
  targetEnvironment?: string | null;
  targetFunction?: string | null;
  strict?: boolean;
  needsCode?: boolean;
  outputCap?: number | null;
  outputReadBytes?: number | null;
  outputDroppedBytes?: number | null;
  outputRefusedBytes?: number | null;
  outputMissingFromSeq?: number | null;
  outputChunks?: number | null;
  outputComplete?: boolean | null;
  testsPassed?: number | null;
  testsFailed?: number | null;
  testsSkipped?: number | null;
  /** Who allowed or denied the run and when; null while it waits. A run a grant or a job allowed names the owner of that grant or job. Absent on an older API. */
  decidedByName?: string | null;
  decidedAt?: string | null;
  /** ADR-0020 §4: with an access_ error, the layer that refused the run and the policy version; absent on an older API. */
  refusedBy?: string | null;
  accessVersion?: number | null;
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

export interface RemoteSettings { remoteRunsEnabled: boolean; alertCpuPct: number; alertMemoryPct: number; alertDiskPct: number; alertSustainSeconds: number; scriptOutputMaxBytes?: number; remoteSchedulesEnabled?: boolean }

/** ADR-0010 §3: an approved job run on a schedule by the API. */
export interface ScheduleView {
  id: string;
  jobId: string;
  jobName: string;
  targetAgentId: string;
  kind: "interval" | "daily" | string;
  intervalMinutes: number | null;
  dailyAtMinutes: number | null;
  status: "active" | "paused" | "ended" | string;
  pauseReason: string | null;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastRunStatus: string | null;
  failuresInARow: number;
  ownerUserId: string;
  isOwner: boolean;
  createdAt: string;
}

export interface ScheduleCreate { jobId: string; kind: "interval" | "daily"; intervalMinutes?: number; dailyAtMinutes?: number; code?: string }

/** ADR-0010 §4: the public half of an approval key a person registered. */
export interface ApprovalKeyView { id: string; name: string; fingerprint: string; publicKey: string; createdAt: string; retiredAt: string | null }
