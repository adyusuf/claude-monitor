// The API's response shapes for machines, metrics and alerts (ADR-0005). Type-only; re-exported from types.ts.

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
