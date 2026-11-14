import { describe, expect, it } from "vitest";
import type { SessionRow, SessionStatus } from "../api/types";
import { groupSessions, machineStats } from "./sessionGroups";

const row = (id: string, status: SessionStatus, repo: string | null = "main"): SessionRow => ({
  id, harnessKind: "claude_code", title: id, model: null, gitBranch: "main", status, startedAt: "2026-10-05T10:00:00Z", lastEventAt: "2026-10-05T10:00:00Z",
  endedAt: null, projectId: null, projectName: repo, agentId: "agent-alpha", hostname: "alpha", ownerId: "u", ownerName: "O", costUsd: null, openPermissions: 0,
});

const statsOf = (rows: SessionRow[]) => machineStats(groupSessions(rows)[0]!);

describe("what a machine's card summarises", () => {
  it("counts only active sessions as active and only waiting ones as waiting, and every loaded session in the total", () => {
    const stats = statsOf([row("a", "active"), row("b", "active"), row("c", "waiting"), row("d", "idle"), row("e", "ended")]);
    expect(stats).toEqual({ active: 2, waiting: 1, total: 5 });
  });

  it("adds up the sessions of every repository on the machine", () => {
    const stats = statsOf([row("a", "active", "one"), row("b", "active", "two"), row("c", "waiting", null)]);
    expect(stats).toEqual({ active: 2, waiting: 1, total: 3 });
  });

  it("is all zeroes for a machine that is listed but has no session", () => {
    const [group] = groupSessions([], [{ agentId: "agent-alpha", hostname: "alpha" }]);
    expect(machineStats(group!)).toEqual({ active: 0, waiting: 0, total: 0 });
  });

  it("does not count idle or ended sessions as active, so a machine with only those shows no active session", () => {
    expect(statsOf([row("a", "idle"), row("b", "ended")])).toEqual({ active: 0, waiting: 0, total: 2 });
  });
});
