import { describe, expect, it } from "vitest";
import type { SessionRow } from "../api/types";
import { groupSessions, orderOf } from "./sessionGroups";

const row = (id: string, hostname: string, gitBranch: string | null, lastEventAt: string): SessionRow => ({
  id, harnessKind: "claude_code", title: id, model: null, gitBranch, status: "idle", startedAt: lastEventAt, lastEventAt, endedAt: null,
  projectId: null, projectName: null, agentId: "a", hostname, ownerId: "u", ownerName: "O", costUsd: null, openPermissions: 0,
});

const ids = (rows: SessionRow[], hostname: string) => groupSessions(rows).find((g) => g.hostname === hostname)!.branches.flatMap((b) => b.rows.map((s) => s.id));

describe("grouping sessions", () => {
  it("groups by machine, then branch, and orders a machine's branches and sessions by the newest activity first", () => {
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

  it("keeps the machines in the same order by name, however recent their activity is and whatever order the rows came in", () => {
    const early = row("a", "alpha", "x", "2026-10-01T00:00:00Z");
    const late = row("z", "Zulu", "x", "2026-10-09T00:00:00Z");
    const mid = row("m", "mike", "x", "2026-10-05T00:00:00Z");
    for (const rows of [[early, late, mid], [late, mid, early], [mid, early, late]]) {
      expect(groupSessions(rows).map((g) => g.hostname)).toEqual(["alpha", "mike", "Zulu"]); // case does not move one
    }
    const newest = row("n", "alpha", "x", "2026-10-20T00:00:00Z"); // the first machine gets the newest activity
    expect(groupSessions([newest, late, mid]).map((g) => g.hostname)).toEqual(["alpha", "mike", "Zulu"]);
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

describe("keeping a machine's order while it is pointed at", () => {
  const before = [
    row("old", "alpha", "main", "2026-10-05T08:00:00Z"),
    row("mid", "alpha", "main", "2026-10-05T09:00:00Z"),
    row("top", "alpha", "main", "2026-10-05T10:00:00Z"),
    row("side", "alpha", "dev", "2026-10-05T07:00:00Z"),
    row("other", "beta", "main", "2026-10-05T06:00:00Z"),
    row("other2", "beta", "main", "2026-10-05T05:00:00Z"),
  ];

  it("keeps the rows and branches where they were though the activity changed, and still shows the new data", () => {
    const frozen = { alpha: orderOf(groupSessions(before).find((g) => g.hostname === "alpha")!) };
    expect(ids(before, "alpha")).toEqual(["top", "mid", "old", "side"]);

    const after = before.map((s) => (s.id === "old" ? { ...s, lastEventAt: "2026-10-05T12:00:00Z", title: "changed" } : s.id === "side" ? { ...s, lastEventAt: "2026-10-05T13:00:00Z" } : s));
    const free = groupSessions(after).find((g) => g.hostname === "alpha")!;
    expect(free.branches.map((b) => b.branch)).toEqual(["dev", "main"]); // without the freeze it moved
    expect(free.branches[1]!.rows.map((s) => s.id)).toEqual(["old", "top", "mid"]);

    const kept = groupSessions(after, frozen).find((g) => g.hostname === "alpha")!;
    expect(kept.branches.map((b) => b.branch)).toEqual(["main", "dev"]);
    expect(kept.branches[0]!.rows.map((s) => s.id)).toEqual(["top", "mid", "old"]);
    expect(kept.branches[0]!.rows.find((s) => s.id === "old")).toMatchObject({ title: "changed", lastEventAt: "2026-10-05T12:00:00Z" }); // current data
  });

  it("puts what was not there when the order was kept after it, newest first, and leaves out what is gone", () => {
    const frozen = { alpha: orderOf(groupSessions(before).find((g) => g.hostname === "alpha")!) };
    const after = [...before.filter((s) => s.id !== "mid"), row("n1", "alpha", "main", "2026-10-05T14:00:00Z"), row("n2", "alpha", "main", "2026-10-05T15:00:00Z"), row("nb", "alpha", "new", "2026-10-05T16:00:00Z")];
    const kept = groupSessions(after, frozen).find((g) => g.hostname === "alpha")!;
    expect(kept.branches.map((b) => b.branch)).toEqual(["main", "dev", "new"]);
    expect(kept.branches[0]!.rows.map((s) => s.id)).toEqual(["top", "old", "n2", "n1"]);
  });

  it("keeps only the machine that is pointed at: the others go on moving", () => {
    const frozen = { alpha: orderOf(groupSessions(before).find((g) => g.hostname === "alpha")!) };
    const after = before.map((s) => (s.id === "other2" ? { ...s, lastEventAt: "2026-10-05T20:00:00Z" } : s));
    const beta = groupSessions(after, frozen).find((g) => g.hostname === "beta")!;
    expect(beta.branches[0]!.rows.map((s) => s.id)).toEqual(["other2", "other"]);
  });
});
