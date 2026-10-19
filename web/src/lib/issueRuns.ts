// Remote runs linked to issues (ADR-0011): which runs may be attached and which ones call for a bug. Pure functions.
import type { WebRunView } from "../api/types";

export const RUN_KINDS: readonly string[] = ["test", "build", "deploy", "other"];

/** A run that ran to its end; only such a run has an outcome worth attaching to an issue. */
const FINISHED: readonly string[] = ["succeeded", "failed", "timed_out"];
export const isFinishedRun = (run: { status: string }): boolean => FINISHED.includes(run.status);

/** A test run with failures or any run that exited with a non-zero code: the runs a bug is offered for. */
export const runFailed = (run: Pick<WebRunView, "testsFailed" | "exitCode">): boolean =>
  (run.testsFailed ?? 0) > 0 || (run.exitCode !== null && run.exitCode !== 0);

/** The runs an issue can still take: finished, not linked yet, test runs first, then newest first. */
export function attachableRuns(runs: WebRunView[], linked: ReadonlySet<string>): WebRunView[] {
  const newest = (a: WebRunView, b: WebRunView) => b.createdAt.localeCompare(a.createdAt);
  const open = runs.filter((r) => isFinishedRun(r) && !linked.has(r.id)).sort(newest);
  return [...open.filter((r) => r.kind === "test"), ...open.filter((r) => r.kind !== "test")];
}
