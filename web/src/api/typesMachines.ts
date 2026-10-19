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
  /** Whether the machine's owner lets Claude be started and stopped on it from the web (ADR-0014). */
  acceptsClaudeControl?: boolean | null;
  /** What the daemon does by itself with Claude (ADR-0014 P3): "off" or "on_message". */
  claudeAutoMode?: ClaudeAutoMode | null;
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
  /** A test command was set at the machine for this project (ADR-0011 §7a); the command and its folder are sent to the machine's owner only. */
  hasTest?: boolean; testArgv?: string[] | null; testCwd?: string | null;
}
/** Everything about one machine: the list entry, the admin's note, the services the caller may see, and the projects. */
export interface MachineDetail {
  machine: MachineView; note: string | null; noteSetAt: string | null; servicesShared: boolean;
  services: ServiceInfo[] | null; servicesAt: string | null; projects: MachineProjectView[];
  /** What Claude Code has on the machine (ADR-0011 §7a, step E); sent to the machine's owner and to admins only, null for everyone else and for an older agent. */
  inventory?: ClaudeInventory | null;
  /** The machine owner has switched the inventory on; while it is off the agent reads and sends nothing. */
  inventoryShared?: boolean;
}
export interface InventorySession { name: string | null; projectKey: string | null; kind: string | null; status: string | null; startedAt: string | null }
export interface InventoryPlugin { id: string; version: string | null; enabled: boolean; scope: string | null }
export interface InventorySetting { projectKey: string; mode: string | null; modeModel: string | null; gates: Record<string, boolean> | null }
/** One usage window of Claude Code: use in per cent (a spend limit may pass 100) and when the window resets. */
export interface ClaudeLimitWindow { usedPercentage: number; resetsAt: string | null }
/** The limits Claude Code last gave to `aipod statusline` (ADR-0011 §7a): they exist only for claude.ai Pro and Max logins and after a session has answered once; seenAt is when. */
export interface ClaudeLimits { fiveHour: ClaudeLimitWindow | null; sevenDay: ClaudeLimitWindow | null; spend: ClaudeLimitWindow | null; seenAt: string }
export interface ClaudeInventory {
  sessions: InventorySession[] | null; plugins: InventoryPlugin[] | null; skills: string[] | null; settings: InventorySetting[] | null; at: string;
  limits?: ClaudeLimits | null;
}
export interface AgentInventoryView { agentId: string; share: boolean; setAt: string | null; setBy: string | null }
export interface AgentServicesView { agentId: string; share: boolean; setAt: string | null; setBy: string | null }

export type AskStatus = "queued" | "delivered" | "answered" | "no_idle_session" | "not_accepting" | "no_answer" | "expired" | "cancelled";
/** One ask to a machine (ADR-0008 §3f); the question and the answer are untrusted text and are sent only to the user they belong to. */
export interface AskView {
  id: string; targetAgentId: string; targetHostname: string; status: AskStatus; question: string | null; answer: string | null; answerTruncated: boolean;
  error: string | null; createdAt: string; expiresAt: string; deliveredAt: string | null; answeredAt: string | null;
}
export interface AgentAsksView { agentId: string; accept: boolean; setAt: string | null; setBy: string | null }

export type AgentActionKind = "restart" | "update_check" | "update" | "status" | "claude_start" | "claude_stop"
  | "plugin_enable" | "plugin_disable" | "plugin_update" | "plugin_install" | "project_setting";
export type ClaudePermissionMode = "manual" | "plan" | "acceptEdits";
export type ClaudeAutoMode = "off" | "on_message";
export interface AgentClaudeView { agentId: string; accepts: boolean; setAt: string | null; setBy: string | null; autoMode?: ClaudeAutoMode }
export type AgentActionStatus = "queued" | "delivered" | "succeeded" | "failed" | "expired";
/** One fixed action its owner asked of a machine's agent (ADR-0005); the result is plain text the agent wrote, never markup. */
export interface AgentActionView {
  id: string; agentId: string; kind: AgentActionKind; status: AgentActionStatus; result: string | null;
  createdAt: string; expiresAt: string; finishedAt: string | null;
  /** Claude control (ADR-0014): the project and mode of a start, the short id of the session a start returned or a stop names. */
  projectKey?: string | null; permissionMode?: ClaudePermissionMode | null; sessionRef?: string | null;
  /** A remote write (ADR-0011 §7a): the plugin id or the setting name, and the setting's value. */
  target?: string | null; value?: string | null;
}
