import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { ApprovalRequestRow } from "../api/types";
import { config } from "../config";
import { isCatchUp, useLive } from "./live";

/**
 * The permission requests waiting on the viewer's own sessions in this workspace (ADR-0019), oldest first. Read again when the workspace's live
 * stream (shared with the page, F-098) says a permission request changed or it caught up, and whenever `refreshKey` changes (a page change); every
 * approvalPollMs only while the stream is not connected. A failed read keeps what was shown, since the band is a hint and each session page shows
 * the real state.
 */
export function useApprovalQueue(workspaceId: string | undefined, refreshKey: string): { rows: ApprovalRequestRow[]; reload: () => void; drop: (id: string) => void } {
  // The rows are kept with the workspace they were read for: after a switch the band shows nothing until the new one answers.
  const [read, setRead] = useState<{ ws: string; rows: ApprovalRequestRow[] }>({ ws: "", rows: [] });
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((n) => n + 1), []);
  const drop = useCallback((id: string) => setRead((r) => ({ ws: r.ws, rows: r.rows.filter((x) => x.id !== id) })), []);
  const connected = useLive(workspaceId, (m) => {
    if (m.event === "permission" || isCatchUp(m)) reload();
  });
  const streaming = useRef(connected);
  useEffect(() => {
    streaming.current = connected;
  }, [connected]);
  useEffect(() => {
    if (!workspaceId) return;
    let live = true;
    const load = () => api.workspacePermissions(workspaceId).then((rows) => live && setRead({ ws: workspaceId, rows })).catch(() => undefined);
    void load();
    // the poll is the fallback: while the stream is connected its messages say when to read
    const timer = setInterval(() => {
      if (!streaming.current) void load();
    }, config.approvalPollMs);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [workspaceId, refreshKey, tick]);
  return { rows: workspaceId && read.ws === workspaceId ? read.rows : [], reload, drop };
}
