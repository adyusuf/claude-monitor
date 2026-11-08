import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import type { AgentRow, MachineView, SessionRow, SessionStatus } from "../api/types";
import { useSession } from "../auth/session";
import { Button, Empty, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { debounce, useLive } from "../lib/live";
import { SessionList } from "./SessionList";

const FILTERS: (SessionStatus | "")[] = ["", "active", "waiting", "idle", "ended"];

/** Machines that answer 404 (an API without remote work) or fail: the page then shows the sessions alone, grouped by their machine's name. */
const orNone = <T,>(read: Promise<T[]>): Promise<T[]> => read.catch((e: unknown) => {
  if (!(e instanceof ApiError && e.status === 404)) console.warn("sessions page: machines could not be read", e);
  return [];
});

/** Every machine of the workspace and, under it, its sessions grouped by repository, the most recent activity first; kept current by the live stream. */
export function SessionsPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [rows, setRows] = useState<SessionRow[] | null>(null);
  const [machines, setMachines] = useState<MachineView[]>([]);
  const [agents, setAgents] = useState<AgentRow[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<SessionStatus | "">("");
  const [error, setError] = useState<unknown>(null);
  const [now, setNow] = useState(() => Date.now());

  const load = useCallback(async () => {
    try {
      const [page, views, list] = await Promise.all([api.sessions(ws, { q: query, status, limit: config.pageSize }), orNone(api.machines(ws)), orNone(api.agents(ws))]);
      setRows(page.items);
      setMachines(views);
      setAgents(list.filter((a) => a.status === "active"));
      setNext(page.next);
      setError(null);
    } catch (e) {
      setError(e);
    }
  }, [ws, query, status]);

  useEffect(() => {
    const timer = setTimeout(() => void load(), query ? config.liveDebounceMs : 0);
    return () => clearTimeout(timer);
  }, [load, query]);

  useEffect(() => {
    const tick = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(tick);
  }, []);

  const filtered = query.trim() !== "" || status !== "";
  const reload = useMemo(() => debounce(() => void load()), [load]);
  const live = useLive(ws, reload);

  const more = async () => {
    if (!next) return;
    const page = await api.sessions(ws, { q: query, status, cursor: next, limit: config.pageSize });
    setRows((r) => [...(r ?? []), ...page.items]);
    setNext(page.next);
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>{t("sessions.title")}</h1>
        <span className={live ? "live on" : "live"}><span className="dot" aria-hidden />{t("sessions.live")}</span>
      </header>
      <div className="toolbar">
        <input className="input search" type="search" placeholder={t("sessions.search")} aria-label={t("sessions.search")}
          value={query} onChange={(e) => setQuery(e.target.value)} />
        <div className="segmented" role="group">
          {FILTERS.map((f) => (
            <button key={f || "all"} type="button" className={status === f ? "seg active" : "seg"} onClick={() => setStatus(f)}>
              {f ? t(`status.${f}`) : t("sessions.all")}
            </button>
          ))}
        </div>
      </div>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 && (filtered || machines.length === 0) ? (
        filtered ? <Empty title={t("sessions.noMatch")} />
          : <Empty title={t("sessions.empty")} hint={t("sessions.emptyHint")}><Link className="btn btn-primary" to="/download">{t("nav.download")}</Link></Empty>
      ) : (
        <SessionList ws={ws} rows={rows} machines={filtered ? [] : machines} agents={agents} userId={me?.id} now={now} complete={next === null}
          onChanged={() => void load()} />
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void more()}>{t("sessions.more")}</Button></div> : null}
    </div>
  );
}
