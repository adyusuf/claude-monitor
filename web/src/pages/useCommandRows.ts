import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { CommandRow } from "../api/types";

/**
 * The commands sent to a session, read again whenever the page's version changes; one list for the conversation and the history.
 * A read that fails keeps the rows already held and reports the error (cleared by the next read that works).
 */
export function useCommandRows(sessionId: string, version: number) {
  const [rows, setRows] = useState<CommandRow[]>([]);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    setRows([]);
    setError(null);
  }, [sessionId]);
  const reload = useCallback(() => api.commands(sessionId).then((read) => {
    setRows(read);
    setError(null);
  }).catch(setError), [sessionId]);
  useEffect(() => {
    void reload();
  }, [reload, version]);
  return { rows, reload, error };
}
