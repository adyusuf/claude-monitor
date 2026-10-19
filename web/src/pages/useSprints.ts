import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { SprintStatus, SprintSummary } from "../api/typesSprints";
import { config } from "../config";
import { useErrorText } from "../i18n";

export type SprintFilter = SprintStatus | "all";

/** The sprints of a project (or of one application), newest first, for a status filter; with paging and the per-card changes (start, delete). */
export function useSprints(projectId: string, applicationId: string, filter: SprintFilter) {
  const errorText = useErrorText();
  const [rows, setRows] = useState<SprintSummary[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef(0);
  const query = { status: filter === "all" ? undefined : filter, application: applicationId || undefined };

  const load = useCallback(async () => {
    const mine = ++latest.current;
    try {
      const page = await api.sprints(projectId, { ...query, limit: config.pageSize });
      if (mine !== latest.current) return;
      setRows(page.items);
      setNext(page.next);
      setError(null);
    } catch (e) {
      if (mine === latest.current) setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, applicationId, filter]);

  useEffect(() => {
    void load();
  }, [load]);

  const more = async () => {
    try {
      const page = await api.sprints(projectId, { ...query, cursor: next, limit: config.pageSize });
      setRows((r) => [...(r ?? []), ...page.items]);
      setNext(page.next);
    } catch (e) {
      setError(errorText(e));
    }
  };

  const act = async (work: () => Promise<unknown>) => {
    try {
      setError(null);
      await work();
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const start = (s: SprintSummary) => act(() => api.startSprint(s.id));
  const remove = (s: SprintSummary) => act(() => api.deleteSprint(s.id));

  return { rows, next, error, more, start, remove };
}
