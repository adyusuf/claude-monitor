import { describe, expect, it } from "vitest";
import { run } from "../test/remote-fixtures";
import { attachableRuns, isFinishedRun, runFailed } from "./issueRuns";

describe("finished runs", () => {
  it.each([["succeeded", true], ["failed", true], ["timed_out", true], ["running", false], ["pending_approval", false], ["denied", false], ["cancelled", false], ["expired", false]])(
    "%s is finished: %s", (status, expected) => expect(isFinishedRun({ status })).toBe(expected),
  );
});

describe("failed runs", () => {
  it.each([
    [{ testsFailed: 2, exitCode: 0 }, true], [{ testsFailed: 0, exitCode: 1 }, true], [{ testsFailed: null, exitCode: 127 }, true],
    [{ testsFailed: 0, exitCode: 0 }, false], [{ testsFailed: null, exitCode: null }, false], [{ testsFailed: undefined, exitCode: 0 }, false],
  ])("%j is failed: %s", (r, expected) => expect(runFailed(r)).toBe(expected));
});

describe("attachable runs", () => {
  it("keeps finished runs that are not linked, test runs first, each group newest first", () => {
    const runs = [
      run({ id: "a", kind: "build", status: "succeeded", createdAt: "2026-03-05T00:00:00Z" }),
      run({ id: "b", kind: "test", status: "failed", createdAt: "2026-03-01T00:00:00Z" }),
      run({ id: "c", kind: "test", status: "succeeded", createdAt: "2026-03-02T00:00:00Z" }),
      run({ id: "d", kind: "test", status: "running", createdAt: "2026-03-06T00:00:00Z" }),
      run({ id: "e", kind: "test", status: "succeeded", createdAt: "2026-03-03T00:00:00Z" }),
      run({ id: "f", status: "failed", createdAt: "2026-03-04T00:00:00Z" }),
    ];
    expect(attachableRuns(runs, new Set(["e"])).map((r) => r.id)).toEqual(["c", "b", "a", "f"]);
  });
});
