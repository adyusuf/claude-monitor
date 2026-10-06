import { render } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router";
import { I18nProvider } from "../i18n";
import type { AlertView, MachineView, MetricSample, WebGrantView, WebJobView, WebRunView } from "../api/types";

/** A run waiting for the signed-in owner, expiring in five minutes; override what the case is about. */
export const run = (over: Partial<WebRunView> = {}): WebRunView => ({
  id: "r1", targetAgentId: "a1", targetHostname: "build-box", targetUser: "svc", requesterAgentId: "a9", requesterHostname: "laptop-9",
  requesterUser: "Diğer Kişi", requesterSessionId: null, visible: true, mode: "argv", argv: ["/usr/bin/ls", "-la"], shellCommand: null, cwd: "/srv/app",
  timeoutSeconds: 60, reason: "list the files", status: "pending_approval", exitCode: null, error: null, outputBytes: 0, outputTruncated: false,
  createdAt: new Date(Date.now() - 60_000).toISOString(), expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(), endedAt: null,
  grantId: null, jobId: null, canDecide: true, selfApproval: false, hash: "hash-1", interpreter: false, recentOutput: false, ...over,
});

export const grant = (over: Partial<WebGrantView> = {}): WebGrantView => ({
  id: "g1", targetAgentId: "a1", ownerUserId: "u1", granteeUserId: "u2", granteeName: "Diğer Kişi", granteeAgentId: null, requestedByAgentId: null,
  requestedByHostname: null, template: ["/usr/bin/ls", "-la"], cwd: "/srv/app", maxTimeoutSeconds: 120, reason: null, status: "active",
  createdAt: new Date(2026, 9, 5, 9, 30).toISOString(), expiresAt: new Date(2026, 9, 12, 9, 30).toISOString(), decidedAt: null, revokedAt: null,
  useCount: 0, lastUsedAt: null, canDecide: false, canRevoke: false, ...over,
});

export const job = (over: Partial<WebJobView> = {}): WebJobView => ({
  id: "j1", targetAgentId: "a1", ownerUserId: "u1", name: "nightly", argv: ["/usr/bin/backup", "--all"], cwd: "/srv", timeoutSeconds: 300, reason: null,
  status: "proposed", proposedByUserId: "u2", proposedByName: "Diğer Kişi", proposedByAgentId: null, proposedByHostname: null,
  createdAt: new Date(2026, 9, 5, 9, 30).toISOString(), decidedAt: null, retiredAt: null, canDecide: false, canRetire: false, ...over,
});

export const sample = (over: Partial<MetricSample> = {}): MetricSample => ({
  sampledAt: new Date(2026, 9, 5, 9, 30, 0).toISOString(), cpuPct: 40, memUsedBytes: 1024, memTotalBytes: 2048,
  disks: [{ mount: "/", usedBytes: 50, totalBytes: 100 }], ...over,
});

export const machine = (over: Partial<MachineView> = {}): MachineView => ({
  agentId: "a1", hostname: "laptop-1", os: "linux", userName: "Diğer Kişi", execLevel: "argv", serviceMode: false, online: true,
  lastSeenAt: new Date(2026, 9, 5, 9, 30).toISOString(), latest: sample(), openAlerts: 0, ...over,
});

export const alert = (over: Partial<AlertView> = {}): AlertView => ({
  id: "al1", agentId: "a1", hostname: "laptop-1", kind: "cpu", subject: "", state: "open", thresholdPct: 90, lastValue: 95, peakValue: 99,
  openedAt: new Date(2026, 9, 5, 9, 30).toISOString(), resolvedAt: null, ...over,
});

const wrap = (ui: ReactNode) => (
  <I18nProvider initial="en">
    <MemoryRouter>{ui}</MemoryRouter>
  </I18nProvider>
);

/** Renders a component that needs only the router and the language (no session, no API of its own). */
export function renderPlain(ui: ReactNode) {
  const result = render(wrap(ui));
  return { ...result, rerender: (next: ReactNode) => result.rerender(wrap(next)) };
}
