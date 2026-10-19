import { describe, expect, it } from "vitest";
import { project, workspaceMembers } from "../test/projects-fixtures";
import { sprintView } from "../test/sprints-fixtures";
import { checkSprint, draftOfSprint, emptySprintDraft, inputOfSprint, teamCandidates } from "./sprintForm";
import { addDays, today, unfinishedCount, unitIsNumeric } from "./sprintOptions";

describe("days", () => {
  it("writes today's calendar day from the clock", () => {
    expect(today(new Date(2026, 0, 5, 23, 59))).toBe("2026-01-05");
  });

  it("adds days across months, years and leap days", () => {
    expect(addDays("2026-03-02", 10)).toBe("2026-03-12");
    expect(addDays("2026-12-25", 14)).toBe("2027-01-08");
    expect(addDays("2028-02-20", 10)).toBe("2028-03-01");
    expect(addDays("2026-03-02", -2)).toBe("2026-02-28");
  });

  it("returns what it cannot read", () => {
    expect(addDays("", 10)).toBe("");
    expect(addDays("soon", 10)).toBe("soon");
  });

  it("knows which units sum", () => {
    expect(unitIsNumeric("hours")).toBe(true);
    expect(unitIsNumeric("t_shirt")).toBe(false);
  });

  it("counts the issues that still need work", () => {
    expect(unfinishedCount({ backlog: 1, ready: 2, in_progress: 3, in_review: 4, done: 5, cancelled: 6 })).toBe(10);
    expect(unfinishedCount({})).toBe(0);
  });
});

describe("the sprint draft", () => {
  it("starts on the given day and ends after the project's sprint length", () => {
    const draft = emptySprintDraft(project({ sprintLengthDays: 14 }), "2026-03-02");
    expect(draft).toMatchObject({ name: "", startDate: "2026-03-02", endDate: "2026-03-16", capacity: "", releaseId: "", teamUserIds: [] });
  });

  it("starts today by default", () => {
    expect(emptySprintDraft(project()).startDate).toBe(today());
  });

  it("is made from a sprint and sent back as numbers and nulls", () => {
    const draft = draftOfSprint(sprintView({}, { releaseId: "r1", capacity: 12.5 }));
    expect(draft).toEqual({ name: "Launch", goal: "Ship the checkout", startDate: "2026-03-02", endDate: "2026-03-13", capacity: "12.5", releaseId: "r1", teamUserIds: ["u2"] });
    expect(inputOfSprint(draft)).toEqual({
      name: "Launch", goal: "Ship the checkout", startDate: "2026-03-02", endDate: "2026-03-13", capacity: 12.5, releaseId: "r1", teamUserIds: ["u2"],
    });
  });

  it("clears what is empty and carries the application only when told", () => {
    const empty = draftOfSprint(sprintView({ team: [] }, { name: null, goal: null, startDate: null, endDate: null, capacity: null }));
    expect(inputOfSprint({ ...empty, name: "  " }, "a1")).toEqual({
      name: null, goal: null, startDate: null, endDate: null, capacity: null, applicationId: "a1", releaseId: null, teamUserIds: [],
    });
    expect(inputOfSprint(empty, null)).toHaveProperty("applicationId", null);
    expect(inputOfSprint(empty)).not.toHaveProperty("applicationId");
  });
});

describe("the checks", () => {
  const ok = emptySprintDraft(project(), "2026-03-02");

  it("accepts a fine draft, a missing end date and a zero capacity", () => {
    expect(checkSprint(ok)).toEqual({});
    expect(checkSprint({ ...ok, endDate: "", capacity: "0" })).toEqual({});
    expect(checkSprint({ ...ok, startDate: "", endDate: "2026-01-01" })).toEqual({});
  });

  it("refuses an end before the start, but accepts the same day", () => {
    expect(checkSprint({ ...ok, endDate: "2026-03-01" })).toEqual({ endDate: "before_start" });
    expect(checkSprint({ ...ok, endDate: "2026-03-02" })).toEqual({});
  });

  it.each(["-1", "abc", "1.234", "100000000"])("refuses the capacity %s", (capacity) => {
    expect(checkSprint({ ...ok, capacity })).toEqual({ capacity: "out_of_range" });
  });

  it("accepts two decimals and the largest capacity", () => {
    expect(checkSprint({ ...ok, capacity: "12.25" })).toEqual({});
    expect(checkSprint({ ...ok, capacity: "99999999" })).toEqual({});
  });

  it("refuses too long a name or goal and too big a team", () => {
    expect(checkSprint({ ...ok, name: "x".repeat(101), goal: "y".repeat(1001) })).toEqual({ name: "too_long", goal: "too_long" });
    expect(checkSprint({ ...ok, name: "x".repeat(100), goal: "y".repeat(1000) })).toEqual({});
    expect(checkSprint({ ...ok, teamUserIds: Array.from({ length: 51 }, (_, i) => `u${i}`) })).toEqual({ teamUserIds: "invalid_list" });
  });
});

describe("the team", () => {
  it("offers members who can work, and a viewer only while in the team", () => {
    expect(teamCandidates(workspaceMembers, []).map((m) => m.userId)).toEqual(["u1", "u2"]);
    expect(teamCandidates(workspaceMembers, ["u3"]).map((m) => m.userId)).toEqual(["u1", "u2", "u3"]);
  });
});
