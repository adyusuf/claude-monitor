import { describe, expect, it } from "vitest";
import type { SessionRow } from "../api/types";
import { groupSessions } from "./sessionGroups";

const row = (id: string, hostname: string, gitBranch: string | null, lastEventAt: string): SessionRow => ({
  id, harnessKind: "claude_code", title: id, model: null, gitBranch, status: "idle", startedAt: lastEventAt, lastEventAt, endedAt: null,
  projectId: null, projectName: null, agentId: "a", hostname, ownerId: "u", ownerName: "O", costUsd: null, openPermissions: 0,
});

describe("grouping sessions", () => {
  it("groups by machine, then branch, and orders every level by the newest activity first", () => {
    const groups = groupSessions([
      row("a", "alpha", "main", "2026-10-05T10:00:00Z"),
      row("b", "beta", "feature", "2026-10-05T12:00:00Z"),
      row("c", "alpha", "dev", "2026-10-05T13:00:00Z"),
      row("d", "alpha", "main", "2026-10-05T11:00:00Z"),
      row("e", "beta", "feature", "2026-10-05T09:00:00Z"),
    ]);
    expect(groups.map((g) => [g.hostname, g.count])).toEqual([["alpha", 3], ["beta", 2]]);
    expect(groups[0]!.branches.map((b) => [b.branch, b.rows.map((s) => s.id)])).toEqual([["dev", ["c"]], ["main", ["d", "a"]]]);
    expect(groups[1]!.branches.map((b) => [b.branch, b.rows.map((s) => s.id)])).toEqual([["feature", ["b", "e"]]]);
  });

  it("puts a machine whose newest session is older after one whose newest is more recent, whatever the order the rows came in", () => {
    const groups = groupSessions([row("a", "old", "x", "2026-10-01T00:00:00Z"), row("b", "new", "x", "2026-10-09T00:00:00Z")]);
    expect(groups.map((g) => g.hostname)).toEqual(["new", "old"]);
  });

  it("keeps sessions with no branch, or a blank one, together under no branch", () => {
    const groups = groupSessions([row("a", "h", null, "2026-10-05T10:00:00Z"), row("b", "h", "  ", "2026-10-05T11:00:00Z"), row("c", "h", "main", "2026-10-05T09:00:00Z")]);
    expect(groups[0]!.branches.map((b) => [b.branch, b.rows.length])).toEqual([[null, 2], ["main", 1]]);
  });

  it("puts a session whose time cannot be read last instead of failing", () => {
    const groups = groupSessions([row("a", "h", "m", "not a time"), row("b", "h", "m", "2026-10-05T10:00:00Z")]);
    expect(groups[0]!.branches[0]!.rows.map((s) => s.id)).toEqual(["b", "a"]);
  });

  it("returns nothing for no sessions", () => {
    expect(groupSessions([])).toEqual([]);
  });
});
