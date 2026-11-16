import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import { config } from "../config";
import { isCatchUp, useLive } from "./live";

/**
 * How many remote runs of the workspace wait for the signed-in user's approval (the runs list already says which the
 * user may decide). Read again when the workspace's live stream (shared with the page, F-098) says a run changed or it caught up, and whenever
 * `refreshKey` changes (a page change); every pendingPollMs only while the stream is not connected. A failed read counts as none, since the badge
 * is only a hint and the machine page shows the real state.
 */
export function usePendingApprovals(workspaceId: string | undefined, refreshKey: string): number {
  const [count, setCount] = useState(0);
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((n) => n + 1), []);
  const connected = useLive(workspaceId, (m) => {
    if (m.event === "run" || isCatchUp(m)) reload();
  });
  const streaming = useRef(connected);
  useEffect(() => {
    streaming.current = connected;
  }, [connected]);
  useEffect(() => {
    if (!workspaceId) return;
    let live = true;
    const load = () => api.runs(workspaceId, { limit: config.runPageSize })
      .then((runs) => live && setCount(runs.filter((r) => r.canDecide && r.status === "pending_approval").length))
      .catch(() => live && setCount(0));
    void load();
    // the poll is the fallback: while the stream is connected its messages say when to read
    const timer = setInterval(() => {
      if (!streaming.current) void load();
    }, config.pendingPollMs);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [workspaceId, refreshKey, tick]);
  return count;
}
