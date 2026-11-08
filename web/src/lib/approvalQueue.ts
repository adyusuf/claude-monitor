import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ApprovalRequestRow } from "../api/types";
import { config } from "../config";

/**
 * The permission requests waiting on the viewer's own sessions in this workspace (ADR-0019), oldest first. Read again every pendingPollMs and
 * whenever `refreshKey` changes (a page change); a failed read keeps what was shown, since the band is a hint and each session page shows the
 * real state.
 */
export function useApprovalQueue(workspaceId: string | undefined, refreshKey: string): { rows: ApprovalRequestRow[]; reload: () => void; drop: (id: string) => void } {
  // The rows are kept with the workspace they were read for: after a switch the band shows nothing until the new one answers.
  const [read, setRead] = useState<{ ws: string; rows: ApprovalRequestRow[] }>({ ws: "", rows: [] });
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((n) => n + 1), []);
  const drop = useCallback((id: string) => setRead((r) => ({ ws: r.ws, rows: r.rows.filter((x) => x.id !== id) })), []);
  useEffect(() => {
    if (!workspaceId) return;
    let live = true;
    const load = () => api.workspacePermissions(workspaceId).then((rows) => live && setRead({ ws: workspaceId, rows })).catch(() => undefined);
    void load();
    const timer = setInterval(() => void load(), config.approvalPollMs);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [workspaceId, refreshKey, tick]);
  return { rows: workspaceId && read.ws === workspaceId ? read.rows : [], reload, drop };
}
