import type { ApprovalRequestRow, ApprovalRuleRow } from "../api/types";

/** A Bash request waiting on the signed-in person's session, expiring at 12:34:56 on 05/10/2099 local time (still open, so it can be answered); override what the case is about. */
export const request = (over: Partial<ApprovalRequestRow> = {}): ApprovalRequestRow => ({
  id: "q1", sessionId: "s1", sessionTitle: "Fix the login", projectName: "widgets", agentId: "a1", hostname: "laptop-1", toolName: "Bash",
  toolInput: { command: "npm test" }, status: "open", createdAt: new Date(2026, 9, 5, 12, 0, 0).toISOString(), expiresAt: new Date(2099, 9, 5, 12, 34, 56).toISOString(),
  decision: null, answeredAt: null, answeredByName: null, answeredByRule: null, canRemember: true, strict: false, ...over,
});

/** A standing approval for one command, ending at 09:30 on 06/10/2099 local time (still live, so the page shows it). */
export const rule = (over: Partial<ApprovalRuleRow> = {}): ApprovalRuleRow => ({
  id: "ru1", agentId: "a1", hostname: "laptop-1", toolName: "Bash", command: "npm test", duration: "1h", createdAt: new Date(2026, 9, 5, 8, 30, 0).toISOString(),
  expiresAt: new Date(2099, 9, 6, 9, 30, 0).toISOString(), useCount: 3, lastUsedAt: null, projectName: "widgets", ...over,
});
