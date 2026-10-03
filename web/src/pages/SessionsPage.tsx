import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { SessionRow, SessionStatus } from "../api/types";
import { Button, Empty, Notice, Spinner, StatusPill } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { age, dateTime, usd } from "../lib/format";
import { debounce, useLive } from "../lib/live";

const FILTERS: (SessionStatus | "")[] = ["", "active", "waiting", "idle", "ended"];

/** The workspace's sessions, newest activity first, kept current by the live stream. */
export function SessionsPage() {
  const { ws = "" } = useParams();
  const { t, dict } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<SessionRow[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<SessionStatus | "">("");
  const [error, setError] = useState<unknown>(null);
  const [now, setNow] = useState(() => Date.now());

  const load = useCallback(async () => {
    try {
      const page = await api.sessions(ws, { q: query, status, limit: config.pageSize });
      setRows(page.items);
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
      {rows === null ? <Spinner /> : rows.length === 0 ? (
        <Empty title={t("sessions.empty")} hint={t("sessions.emptyHint")}><Link className="btn btn-primary" to="/download">{t("nav.download")}</Link></Empty>
      ) : (
        <ul className="session-list">
          {rows.map((s) => (
            <li key={s.id}>
              <Link className="session-row" to={`/w/${ws}/sessions/${s.id}`}>
                <div className="session-main">
                  <div className="session-title">
                    <StatusPill status={s.status} />
                    <span className="title-text">{s.title ?? t("sessions.untitled")}</span>
                    {s.openPermissions > 0 ? <span className="badge warn">{t("sessions.permissionsWaiting", { n: s.openPermissions })}</span> : null}
                  </div>
                  <div className="session-meta muted">
                    {s.projectName ? <span>▸ {s.projectName}{s.gitBranch ? ` · ${s.gitBranch}` : ""}</span> : null}
                    <span>⌂ {s.hostname}</span>
                    <span>◍ {s.ownerName}</span>
                    {s.model ? <span>{s.model}</span> : null}
                  </div>
                </div>
                <div className="session-side">
                  <span className="cost">{usd(s.costUsd, t("sessions.unmeasured"))}</span>
                  <span className="muted" title={dateTime(s.lastEventAt)}>{age(s.lastEventAt, now, dict.time)}</span>
                </div>
              </Link>
            </li>
          ))}
        </ul>
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void more()}>{t("sessions.more")}</Button></div> : null}
    </div>
  );
}
