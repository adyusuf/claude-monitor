import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { IssueSummary } from "../api/typesIssues";
import type { SprintView } from "../api/typesSprints";
import { config } from "../config";
import { useErrorText } from "../i18n";

/**
 * One sprint and its issues, with the way every change goes: do the call, then read the sprint and its issues again so the counts, the sums and
 * the columns are the API's. `version` goes up with each reading so what depends on the backlog (the issues that can still be added) reads again too.
 */
export function useSprintView(sprintId: string, projectId: string) {
  const errorText = useErrorText();
  const [view, setView] = useState<SprintView | null>(null);
  const [issues, setIssues] = useState<IssueSummary[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const latest = useRef(0);

  const refresh = useCallback(async () => {
    const mine = ++latest.current;
    const [sprint, page] = await Promise.all([api.sprint(sprintId), api.issues(projectId, { sprint: sprintId, limit: config.pageSize })]);
    if (mine !== latest.current) return;
    setView(sprint);
    setIssues(page.items);
    setNext(page.next);
    setVersion((v) => v + 1);
  }, [sprintId, projectId]);

  useEffect(() => {
    refresh().catch((e: unknown) => setError(errorText(e)));
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refresh]);

  /** Runs a change; true when it went through. `reload: false` is for a change after which the sprint is gone (a deletion). */
  const act = async (work: () => Promise<unknown>, reload = true): Promise<boolean> => {
    try {
      setError(null);
      await work();
      if (reload) await refresh();
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    }
  };

  const more = async () => {
    try {
      const page = await api.issues(projectId, { sprint: sprintId, cursor: next, limit: config.pageSize });
      setIssues((rows) => [...(rows ?? []), ...page.items]);
      setNext(page.next);
    } catch (e) {
      setError(errorText(e));
    }
  };

  return { view, issues, next, error, setError, version, refresh, act, more };
}
