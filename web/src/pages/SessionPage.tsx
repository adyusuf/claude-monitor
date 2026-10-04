import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { SessionDetail } from "../api/types";
import { Card, Notice, Spinner, StatusPill } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { count, dateTime, usd } from "../lib/format";
import { debounce, useLive } from "../lib/live";
import { CommandsCard, PermissionsCard } from "./SessionControls";
import { EventsCard } from "./SessionEvents";

/** One session: what it is doing, what it cost, what waits for a person, and its activity. */
export function SessionPage() {
  const { ws = "", id = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [detail, setDetail] = useState<SessionDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);

  const load = useCallback(async () => {
    try {
      setDetail(await api.session(id));
      setError(null);
    } catch (e) {
      setError(e);
    }
  }, [id]);

  useEffect(() => {
    void load();
  }, [load]);

  const reload = useMemo(() => debounce(() => {
    void load();
    setVersion((v) => v + 1);
  }), [load]);
  useLive(ws, (m) => {
    if (!m.sessionId || m.sessionId === id) reload();
  });

  if (error && !detail) return <div className="page"><Notice kind="error">{errorText(error)}</Notice></div>;
  if (!detail) return <Spinner />;
  const s = detail.session;

  return (
    <div className="page">
      <Link className="back" to={`/w/${ws}/sessions`}>← {t("session.back")}</Link>
      <header className="page-head session-head">
        <div>
          <h1>{s.title ?? t("sessions.untitled")}</h1>
          <div className="session-meta muted">
            {s.projectName ? <span>▸ {s.projectName}</span> : null}
            {s.gitBranch ? <span>{t("session.branch")}: {s.gitBranch}</span> : null}
            <span>⌂ {s.hostname}</span>
            <span>◍ {s.ownerName}</span>
            <span>{t("session.started")}: {dateTime(s.startedAt)}</span>
          </div>
        </div>
        <div className="head-side">
          <StatusPill status={s.status} />
          <span className="cost big">{usd(s.costUsd, t("sessions.unmeasured"))}</span>
        </div>
      </header>

      <div className="grid">
        <div className="col">
          <PermissionsCard sessionId={id} canAnswer={detail.canCommand} version={version} onChange={reload} />
          <CommandsCard sessionId={id} canCommand={detail.canCommand} ended={s.status === "ended"} version={version} onChange={reload} />
          <EventsCard sessionId={id} version={version} />
        </div>
        <div className="col side">
          <Card title={t("session.tasks")}>
            {detail.tasks.length === 0 ? <p className="muted">{t("session.noTasks")}</p> : (
              <ul className="tasks">
                {detail.tasks.map((task) => (
                  <li key={task.id} className={`task task-${task.status}`}>
                    <span className="task-box" aria-hidden>{task.status === "completed" ? "✓" : task.status === "in_progress" ? "◐" : "○"}</span>
                    <span>{task.subject}</span>
                  </li>
                ))}
              </ul>
            )}
          </Card>
          <Card title={t("session.subagents")}>
            {detail.subagents.length === 0 ? <p className="muted">{t("session.noSubagents")}</p> : (
              <ul className="plain">
                {detail.subagents.map((r) => (
                  <li key={r.id}>
                    <strong>{r.agentType}</strong> <span className="muted">{r.status === "running" ? "…" : "✓"} {dateTime(r.startedAt)}</span>
                    {r.description ? <div className="muted small">{r.description}</div> : null}
                  </li>
                ))}
              </ul>
            )}
          </Card>
          <Card title={t("session.usage")}>
            <table className="table compact">
              <thead><tr><th>{t("session.model")}</th><th>{t("session.input")}</th><th>{t("session.output")}</th><th>{t("session.cacheRead")}</th><th>{t("sessions.cost")}</th></tr></thead>
              <tbody>
                {detail.usage.map((u) => (
                  <tr key={u.model}>
                    <td>{u.model}</td><td>{count(u.inputTokens)}</td><td>{count(u.outputTokens)}</td><td>{count(u.cacheReadTokens)}</td>
                    <td>{usd(u.costUsd, t("sessions.unmeasured"))}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Card>
        </div>
      </div>
    </div>
  );
}
