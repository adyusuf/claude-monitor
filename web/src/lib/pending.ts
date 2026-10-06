import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import { config } from "../config";

/**
 * How many remote runs of the workspace wait for the signed-in user's approval (the runs list already says which the
 * user may decide). Read again every pendingPollMs and whenever `refreshKey` changes (a page change); a failed read
 * counts as none, since the badge is only a hint and the machine page shows the real state.
 */
export function usePendingApprovals(workspaceId: string | undefined, refreshKey: string): number {
  const [count, setCount] = useState(0);
  useEffect(() => {
    if (!workspaceId) return;
    let live = true;
    const load = () => api.runs(workspaceId, { limit: config.runPageSize })
      .then((runs) => live && setCount(runs.filter((r) => r.canDecide && r.status === "pending_approval").length))
      .catch(() => live && setCount(0));
    void load();
    const timer = setInterval(() => void load(), config.pendingPollMs);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [workspaceId, refreshKey]);
  return count;
}
