import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/endpoints";
import type { AlertView, MachineView, MetricSample, WebGrantView, WebJobView, WebRunView } from "../api/types";
import { config } from "../config";

export interface MachineData {
  /** Null until loaded, and when the workspace has no such machine. */
  machine: MachineView | null;
  loaded: boolean;
  /** True when the signed-in user owns this machine's agent (only the owner gives grants). */
  owned: boolean;
  metrics: MetricSample[];
  alerts: AlertView[];
  runs: WebRunView[];
  grants: WebGrantView[];
  jobs: WebJobView[];
  hasMoreRuns: boolean;
  error: unknown;
  /** Reads everything again (a live message, or a decision just made). */
  reload: () => Promise<void>;
  moreRuns: () => Promise<void>;
}

/** Runs of the first page, then the older ones already loaded that the first page does not reach. */
function mergeRuns(first: WebRunView[], previous: WebRunView[]): WebRunView[] {
  const last = first.length > 0 ? new Date(first[first.length - 1]!.createdAt).getTime() : Infinity;
  const seen = new Set(first.map((r) => r.id));
  return [...first, ...previous.filter((r) => !seen.has(r.id) && new Date(r.createdAt).getTime() < last)];
}

/** Everything the machine page shows, read from the API; the metrics also refresh on their own (the agent reports every minute). */
export function useMachineData(ws: string, agentId: string, userId: string | undefined, showResolved: boolean): MachineData {
  const [machine, setMachine] = useState<MachineView | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [owned, setOwned] = useState(false);
  const [metrics, setMetrics] = useState<MetricSample[]>([]);
  const [alerts, setAlerts] = useState<AlertView[]>([]);
  const [runs, setRuns] = useState<WebRunView[]>([]);
  const [grants, setGrants] = useState<WebGrantView[]>([]);
  const [jobs, setJobs] = useState<WebJobView[]>([]);
  const [hasMoreRuns, setHasMoreRuns] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const pagedOlder = useRef(false);

  const reload = useCallback(async () => {
    try {
      const [machines, agents, alertRows, runRows, grantRows, jobRows] = await Promise.all([
        api.machines(ws), api.agents(ws), api.alerts(ws, agentId, showResolved),
        api.runs(ws, { agent: agentId, limit: config.runPageSize }), api.grants(agentId), api.jobs(agentId),
      ]);
      setMachine(machines.find((m) => m.agentId === agentId) ?? null);
      setOwned(agents.some((a) => a.id === agentId && a.userId === userId));
      setAlerts(alertRows);
      setRuns((previous) => mergeRuns(runRows, previous));
      if (!pagedOlder.current) setHasMoreRuns(runRows.length >= config.runPageSize);
      setGrants(grantRows);
      setJobs(jobRows);
      setError(null);
    } catch (e) {
      setError(e);
    } finally {
      setLoaded(true);
    }
  }, [ws, agentId, userId, showResolved]);

  const loadMetrics = useCallback(async () => {
    try {
      setMetrics(await api.metrics(agentId, config.metricsMinutes));
    } catch (e) {
      setError(e);
    }
  }, [agentId]);

  const moreRuns = useCallback(async () => {
    const oldest = runs[runs.length - 1];
    if (!oldest) return;
    try {
      const page = await api.runs(ws, { agent: agentId, before: oldest.createdAt, limit: config.runPageSize });
      pagedOlder.current = true;
      setRuns((previous) => [...previous, ...page.filter((r) => !previous.some((p) => p.id === r.id))]);
      setHasMoreRuns(page.length >= config.runPageSize);
    } catch (e) {
      setError(e);
    }
  }, [ws, agentId, runs]);

  useEffect(() => {
    void reload();
  }, [reload]);

  useEffect(() => {
    void loadMetrics();
    const timer = setInterval(() => void loadMetrics(), config.metricsRefreshMs);
    return () => clearInterval(timer);
  }, [loadMetrics]);

  return { machine, loaded, owned, metrics, alerts, runs, grants, jobs, hasMoreRuns, error, reload, moreRuns };
}
