import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { SessionDetail } from "../api/types";
import { Card, Notice, Spinner, StatusPill } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { count, dateTime, usd } from "../lib/format";
import { debounce, isCatchUp, useLive } from "../lib/live";
import { CommandsCard, Composer, PermissionsCard } from "./SessionControls";
import { useCommandRows } from "./useCommandRows";
import { ChatCard } from "./ChatView";
import { EventsCard } from "./SessionEvents";
import { SubagentsCard } from "./SubagentsCard";

/** One session: what it is doing, what it cost, what waits for a person, and its activity. */
export function SessionPage() {
  const { ws = "", id = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [detail, setDetail] = useState<SessionDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);
  const [view, setView] = useState<"chat" | "raw">("chat");
  const { rows, reload: reloadRows } = useCommandRows(id, version);

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
    if (!m.sessionId || m.sessionId === id || isCatchUp(m)) reload();
  });

  if (error && !detail) return <div className="page"><Notice kind="error">{errorText(error)}</Notice></div>;
  if (!detail) return <Spinner />;
  const s = detail.session;
  const sender = { sessionId: id, canCommand: detail.canCommand, ended: s.status === "ended", lastEventAt: s.lastEventAt, pushTransport: s.pushTransport, reloadRows, onChange: reload };

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
          <div className="seg" role="group" aria-label={t("chat.view")}>
            {(["chat", "raw"] as const).map((v) => (
              <button key={v} type="button" className={view === v ? "seg-on" : ""} aria-pressed={view === v} onClick={() => setView(v)}>{t(`chat.view_${v}`)}</button>
            ))}
          </div>
          {view === "chat"
            ? <ChatCard sessionId={id} version={version} canReply={detail.canCommand} onReplied={reload} commands={rows} canCancel={detail.canCommand}
              onCancel={(commandId) => { void api.cancelCommand(commandId).then(reloadRows).catch(() => reloadRows()); }}
              footer={<>
                <PermissionsCard inline sessionId={id} canAnswer={detail.canCommand} version={version} onChange={reload} />
                <Composer {...sender} />
              </>} />
            : <>
              <EventsCard sessionId={id} owner={{ ownerId: s.ownerId, ownerName: s.ownerName }} version={version} />
              <PermissionsCard sessionId={id} canAnswer={detail.canCommand} version={version} onChange={reload} />
            </>}
          <CommandsCard {...sender} rows={rows} withComposer={view === "raw"} />
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
          <SubagentsCard rows={detail.subagents} />
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
