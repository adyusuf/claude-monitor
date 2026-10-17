import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { CommandRow } from "../api/types";

/** The commands sent to a session, read again whenever the page's version changes; one list for the conversation and the history. */
export function useCommandRows(sessionId: string, version: number) {
  const [rows, setRows] = useState<CommandRow[]>([]);
  const reload = useCallback(() => api.commands(sessionId).then(setRows).catch(() => setRows([])), [sessionId]);
  useEffect(() => {
    void reload();
  }, [reload, version]);
  return { rows, reload };
}
