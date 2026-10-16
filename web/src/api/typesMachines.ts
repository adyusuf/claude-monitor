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
