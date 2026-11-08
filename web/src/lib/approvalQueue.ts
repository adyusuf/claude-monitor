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
  const [rows, setRows] = useState<ApprovalRequestRow[]>([]);
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((n) => n + 1), []);
  const drop = useCallback((id: string) => setRows((r) => r.filter((x) => x.id !== id)), []);
  useEffect(() => {
    if (!workspaceId) return;
    let live = true;
    const load = () => api.workspacePermissions(workspaceId).then((r) => live && setRows(r)).catch(() => undefined);
    void load();
    const timer = setInterval(() => void load(), config.approvalPollMs);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [workspaceId, refreshKey, tick]);
  return { rows: workspaceId ? rows : [], reload, drop };
}
