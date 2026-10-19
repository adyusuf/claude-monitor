import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { IssueQuery, IssueSummary } from "../api/typesIssues";
import { config } from "../config";
import { useErrorText } from "../i18n";

export type IssueScope = "backlog" | "all" | "archived";

export interface BacklogFilters { q: string; type: string; status: string; assignee: string; application: string; component: string; release: string }

export const noFilters: BacklogFilters = { q: "", type: "", status: "", assignee: "", application: "", component: "", release: "" };

const queryOf = (f: BacklogFilters, scope: IssueScope): IssueQuery => ({
  ...f, scope: scope === "backlog" ? "backlog" : undefined, archived: scope === "archived" ? true : undefined,
});

/** The issues of a project in backlog order for the chosen filters and scope, with paging and the per-row changes (move, archive, restore). */
export function useBacklog(projectId: string, filters: BacklogFilters, scope: IssueScope) {
  const errorText = useErrorText();
  const [rows, setRows] = useState<IssueSummary[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef(0);
  const query = useMemo(() => queryOf(filters, scope), [filters, scope]);

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
  }, [projectId, query]);

  useEffect(() => {
    const timer = setTimeout(() => void load(), filters.q ? config.liveDebounceMs : 0);
    return () => clearTimeout(timer);
  }, [load, filters.q]);

  const more = async () => {
    try {
      const page = await api.issues(projectId, { ...query, cursor: next, limit: config.pageSize });
      setRows((r) => [...(r ?? []), ...page.items]);
      setNext(page.next);
    } catch (e) {
      setError(errorText(e));
    }
  };

  const run = async (work: () => Promise<unknown>, done: () => void) => {
    try {
      setError(null);
      await work();
      done();
    } catch (e) {
      setError(errorText(e));
    }
  };

  /** One place up (-1) or down (+1): the API wants the issue to sit after, or null for the top. */
  const move = (index: number, direction: -1 | 1) => {
    const list = rows ?? [];
    const issue = list[index];
    const target = list[index + direction];
    if (!issue || !target) return Promise.resolve();
    const after = direction < 0 ? (list[index - 2]?.id ?? null) : target.id;
    return run(() => api.moveIssue(issue.id, after), () => setRows((r) => {
      const copy = [...(r ?? [])];
      const from = copy.findIndex((x) => x.id === issue.id);
      const to = from + direction;
      if (from >= 0 && to >= 0 && to < copy.length) [copy[from], copy[to]] = [copy[to]!, copy[from]!];
      return copy;
    }));
  };

  const drop = (id: string) => () => setRows((r) => r?.filter((x) => x.id !== id) ?? r);
  const archive = (issue: IssueSummary) => run(() => api.archiveIssue(issue.id), drop(issue.id));
  const restore = (issue: IssueSummary) => run(() => api.restoreIssue(issue.id), drop(issue.id));

  return { rows, next, error, setError, more, move, archive, restore };
}
