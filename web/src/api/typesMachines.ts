// The API's response shapes for machines, metrics and alerts (ADR-0005). Type-only; re-exported from types.ts.

export type ExecLevel = "off" | "argv" | "shell";
export interface DiskSample { mount: string; usedBytes: number; totalBytes: number }
export interface MetricSample { sampledAt: string; cpuPct: number; memUsedBytes: number; memTotalBytes: number; disks: DiskSample[] }
export type MachineEnvironment = "dev" | "test" | "stage" | "preprod" | "production" | "other";
export type MachineFunction = "development" | "application" | "web" | "database" | "ci" | "storage" | "other";
export type ClaudeKind = "none" | "npm" | "native" | "unsupported";
/** One tool of the inventory (ADR-0008 §3a′): home-relative path, the first line of --version (null: not run), and the ADR-0005 trust verdict. */
export interface ToolInfo { name: string; path: string; version: string | null; trusted: boolean }
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
  // ADR-0008: the role an admin set, the agent's claim, the effective role the server computed, Claude presence and the inventory.
  // Every field is optional: an older API omits them, an older agent leaves them null (shown as "unknown").
  roleEnvironment?: MachineEnvironment | null;
  roleFunction?: MachineFunction | null;
  claimedEnvironment?: MachineEnvironment | null;
  claimedFunction?: MachineFunction | null;
  effectiveEnvironment?: MachineEnvironment | null;
  effectiveFunction?: MachineFunction | null;
  claudeKind?: ClaudeKind | null;
  claudeVersion?: string | null;
  claudeSessionsIdle?: number | null;
  interpreters?: ToolInfo[] | null;
  tools?: ToolInfo[] | null;
  capabilities?: string[] | null;
  presenceAt?: string | null;
  /** The admin's key=value tags (ADR-0008 §3a′); an older API omits them. */
  tags?: Record<string, string> | null;
  /** Whether the machine's owner lets their other machines ask a Claude session here (ADR-0008 §3f). */
  acceptsAsks?: boolean | null;
}
export interface AgentRoleInput { environment: MachineEnvironment | string | null; function: MachineFunction | string | null; code?: string }
export interface AgentRoleView {
  agentId: string; environment: MachineEnvironment | null; function: MachineFunction | null; setAt: string | null; setBy: string | null;
  claimedEnvironment: MachineEnvironment | null; claimedFunction: MachineFunction | null; effectiveEnvironment: MachineEnvironment | null; effectiveFunction: MachineFunction | null;
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

/** One listening TCP port; the process name is empty when the OS did not show the owner to the agent. */
export interface ServiceInfo { port: number; proto: string; process: string | null }
/** One run of a project on a machine (ADR-0008 §3a′); the counts are null when the test tool was not recognised. */
export interface ProjectRunSummary {
  kind: string | null; status: string; exitCode: number | null; at: string; outputTruncated: boolean;
  testsPassed: number | null; testsFailed: number | null; testsSkipped: number | null;
}
export interface MachineProjectView {
  key: string; name: string; branch: string | null; headSha: string | null; tag: string | null; lastSeenAt: string;
  source: "session" | "listed"; lastRuns: ProjectRunSummary[]; recentErrors: number;
}
/** Everything about one machine: the list entry, the admin's note, the services the caller may see, and the projects. */
export interface MachineDetail {
  machine: MachineView; note: string | null; noteSetAt: string | null; servicesShared: boolean;
  services: ServiceInfo[] | null; servicesAt: string | null; projects: MachineProjectView[];
}
export interface AgentServicesView { agentId: string; share: boolean; setAt: string | null; setBy: string | null }

export type AskStatus = "queued" | "delivered" | "answered" | "no_idle_session" | "not_accepting" | "no_answer" | "expired" | "cancelled";
/** One ask to a machine (ADR-0008 §3f); the question and the answer are untrusted text and are sent only to the user they belong to. */
export interface AskView {
  id: string; targetAgentId: string; targetHostname: string; status: AskStatus; question: string | null; answer: string | null; answerTruncated: boolean;
  error: string | null; createdAt: string; expiresAt: string; deliveredAt: string | null; answeredAt: string | null;
}
export interface AgentAsksView { agentId: string; accept: boolean; setAt: string | null; setBy: string | null }
