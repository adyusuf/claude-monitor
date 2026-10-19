import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { IssueSummary } from "../api/typesIssues";
import { config } from "../config";
import { useErrorText } from "../i18n";

/**
 * The issues that can still go into a sprint: those of the backlog (in no sprint, backlog or ready), for an application's sprint only that
 * application's. They are read again whenever `version` changes (after any change to the sprint).
 */
export function useSprintCandidates(projectId: string, applicationId: string | null, version: number) {
  const errorText = useErrorText();
  const [rows, setRows] = useState<IssueSummary[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef(0);
  const query = { scope: "backlog" as const, application: applicationId ?? undefined };

  const load = useCallback(async () => {
    const mine = ++latest.current;
    try {
      const page = await api.issues(projectId, { ...query, limit: config.pageSize });
      if (mine !== latest.current) return;
      setRows(page.items);
      setNext(page.next);
      setError(null);
    } catch (e) {
      if (mine === latest.current) setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, applicationId, version]);

  useEffect(() => {
    void load();
  }, [load]);

  const more = async () => {
    try {
      const page = await api.issues(projectId, { ...query, cursor: next, limit: config.pageSize });
      setRows((r) => [...(r ?? []), ...page.items]);
      setNext(page.next);
    } catch (e) {
      setError(errorText(e));
    }
  };

  return { rows, next, error, more };
}
