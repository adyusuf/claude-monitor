import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import type { AlertView, MachineDetail, MachineView, MetricSample, ScheduleView, WebGrantView, WebJobView, WebRunView } from "../api/types";
import { config } from "../config";

export interface MachineData {
  /** Null until loaded, and when the workspace has no such machine. */
  machine: MachineView | null;
  /** The note, the services the user may see and the projects; null while unread or when the API has no detail. */
  detail: MachineDetail | null;
  loaded: boolean;
  /** True when the signed-in user owns this machine's agent (only the owner gives grants). */
  owned: boolean;
  /** The version the machine's agent reports; null while unread. */
  agentVersion: string | null;
  metrics: MetricSample[];
  alerts: AlertView[];
  runs: WebRunView[];
  grants: WebGrantView[];
  jobs: WebJobView[];
  schedules: ScheduleView[];
  hasMoreRuns: boolean;
  hasMoreGrants: boolean;
  hasMoreJobs: boolean;
  error: unknown;
  /** Reads everything again (a live message, or a decision just made). */
  reload: () => Promise<void>;
  moreRuns: () => Promise<void>;
  moreGrants: () => Promise<void>;
  moreJobs: () => Promise<void>;
}

/** The answers of an API that has no such endpoint yet (an older server), as opposed to a failure. */
const NO_ENDPOINT: readonly number[] = [404, 405, 501];
const noEndpoint = (e: unknown): boolean => e instanceof ApiError && NO_ENDPOINT.includes(e.status);

/** Rows of the first page (runs, grants, jobs), then the older ones already loaded that the first page does not reach. */
function mergePage<T extends { id: string; createdAt: string }>(first: T[], previous: T[]): T[] {
  const last = first.length > 0 ? new Date(first[first.length - 1]!.createdAt).getTime() : Infinity;
  const seen = new Set(first.map((r) => r.id));
  return [...first, ...previous.filter((r) => !seen.has(r.id) && new Date(r.createdAt).getTime() < last)];
}

/** Everything the machine page shows, read from the API; the metrics also refresh on their own (the agent reports every minute). */
export function useMachineData(ws: string, agentId: string, userId: string | undefined, showResolved: boolean): MachineData {
  const [machine, setMachine] = useState<MachineView | null>(null);
  const [detail, setDetail] = useState<MachineDetail | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [owned, setOwned] = useState(false);
  const [agentVersion, setAgentVersion] = useState<string | null>(null);
  const [metrics, setMetrics] = useState<MetricSample[]>([]);
  const [alerts, setAlerts] = useState<AlertView[]>([]);
  const [runs, setRuns] = useState<WebRunView[]>([]);
  const [grants, setGrants] = useState<WebGrantView[]>([]);
  const [jobs, setJobs] = useState<WebJobView[]>([]);
  const [schedules, setSchedules] = useState<ScheduleView[]>([]);
  const [hasMoreRuns, setHasMoreRuns] = useState(false);
  const [hasMoreGrants, setHasMoreGrants] = useState(false);
  const [hasMoreJobs, setHasMoreJobs] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const latestReload = useRef(0);
  const pagedOlder = useRef(false);
  const pagedGrants = useRef(false);
  const pagedJobs = useRef(false);

  const reload = useCallback(async () => {
    // Only the newest read counts: an older one that answers late (another filter, another sign-in state) must not overwrite what the newer one showed.
    const mine = ++latestReload.current;
    try {
      // An older API has no detail or schedules: the page shows what the list gives. Any other failure keeps what was read before and is shown.
      let partial: unknown = null;
      const optional = <T,>(read: Promise<T>, missing: T): Promise<T | undefined> => read.catch((e: unknown) => {
        if (noEndpoint(e)) return missing;
        partial = e;
        return undefined;
      });
      const [machines, agents, alertRows, runRows, grantRows, jobRows, detailRow, scheduleRows] = await Promise.all([
        api.machines(ws), api.agents(ws), api.alerts(ws, agentId, showResolved),
        api.runs(ws, { agent: agentId, limit: config.runPageSize }), api.grants(agentId, { limit: config.remotePageSize }), api.jobs(agentId, { limit: config.remotePageSize }),
        optional<MachineDetail | null>(api.machineDetail(agentId), null),
        optional<ScheduleView[]>(api.schedules(agentId), []),
      ]);
      if (mine !== latestReload.current) return;
      setMachine(machines.find((m) => m.agentId === agentId) ?? null);
      if (detailRow !== undefined) setDetail(detailRow);
      setOwned(agents.some((a) => a.id === agentId && a.userId === userId));
      setAgentVersion(agents.find((a) => a.id === agentId)?.version ?? null);
      setAlerts(alertRows);
      setRuns((previous) => mergePage(runRows, previous));
      if (!pagedOlder.current) setHasMoreRuns(runRows.length >= config.runPageSize);
      setGrants((previous) => mergePage(grantRows, previous));
      if (!pagedGrants.current) setHasMoreGrants(grantRows.length >= config.remotePageSize);
      setJobs((previous) => mergePage(jobRows, previous));
      if (!pagedJobs.current) setHasMoreJobs(jobRows.length >= config.remotePageSize);
      if (scheduleRows !== undefined) setSchedules(scheduleRows);
      setError(partial);
    } catch (e) {
      if (mine === latestReload.current) setError(e);
    } finally {
      if (mine === latestReload.current) setLoaded(true);
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

  const moreGrants = useCallback(async () => {
    const oldest = grants[grants.length - 1];
    if (!oldest) return;
    try {
      const page = await api.grants(agentId, { before: oldest.createdAt, limit: config.remotePageSize });
      pagedGrants.current = true;
      setGrants((previous) => [...previous, ...page.filter((g) => !previous.some((p) => p.id === g.id))]);
      setHasMoreGrants(page.length >= config.remotePageSize);
    } catch (e) {
      setError(e);
    }
  }, [agentId, grants]);

  const moreJobs = useCallback(async () => {
    const oldest = jobs[jobs.length - 1];
    if (!oldest) return;
    try {
      const page = await api.jobs(agentId, { before: oldest.createdAt, limit: config.remotePageSize });
      pagedJobs.current = true;
      setJobs((previous) => [...previous, ...page.filter((j) => !previous.some((p) => p.id === j.id))]);
      setHasMoreJobs(page.length >= config.remotePageSize);
    } catch (e) {
      setError(e);
    }
  }, [agentId, jobs]);

  useEffect(() => {
    void reload();
  }, [reload]);

  useEffect(() => {
    void loadMetrics();
    const timer = setInterval(() => void loadMetrics(), config.metricsRefreshMs);
    return () => clearInterval(timer);
  }, [loadMetrics]);

  return { machine, detail, loaded, owned, agentVersion, metrics, alerts, runs, grants, jobs, schedules, hasMoreRuns, hasMoreGrants, hasMoreJobs, error, reload, moreRuns, moreGrants, moreJobs };
}
