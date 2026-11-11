import { Fragment, useId, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { AgentRow, MachineView, SessionRow } from "../api/types";
import { Icon } from "../components/icons";
import { Button, Notice, StatusPill } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { age, dateTime, usd } from "../lib/format";
import { groupSessions, machineStats, type MachineGroup, type MachineOrder, orderOf } from "../lib/sessionGroups";
import { SessionMachineCard } from "./SessionMachineCard";
import { SessionMachineHead } from "./SessionMachineHead";

/**
 * One session: a link to its page, and beside it whether the web started it and, for its owner, a stop (a session the web started and that still
 * runs) or a remove from the list (an ended one; nothing is deleted).
 */
function SessionItem({ ws, s, now, mine, onChanged }: { ws: string; s: SessionRow; now: number; mine: boolean; onChanged: () => void }) {
  const { t, dict } = useI18n();
  const errorText = useErrorText();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [stopSent, setStopSent] = useState(false);
  const act = async (call: () => Promise<unknown>, then?: () => void) => {
    setBusy(true);
    setError(null);
    try {
      await call();
      then?.();
      onChanged();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  const ended = s.status === "ended";
  const stop = () => {
    if (window.confirm(t("sessions.stopConfirm"))) void act(() => api.requestAgentAction(s.agentId, "claude_stop", undefined, { sessionRef: s.webSessionRef ?? undefined }), () => setStopSent(true));
  };
  const hide = () => {
    if (window.confirm(t("sessions.hideConfirm"))) void act(() => api.hideSession(s.id));
  };
  return (
    <div className="session-row">
      <Link className="session-link" to={`/w/${ws}/sessions/${s.id}`}>
        <div className="session-main">
          <div className="session-title">
            <StatusPill status={s.status} />
            <span className="title-text">{s.title ?? t("sessions.untitled")}</span>
            {s.startedFromWeb ? <span className="badge" title={t("sessions.fromWebHint")}>{t("sessions.fromWeb")}</span> : null}
            {s.openPermissions > 0 ? <span className="badge warn">{t("sessions.permissionsWaiting", { n: s.openPermissions })}</span> : null}
          </div>
          <div className="session-meta muted">
            {s.gitBranch?.trim() ? <span><Icon name="branch" /> {s.gitBranch}</span> : null}
            <span><Icon name="members" /> {s.ownerName}</span>
            {s.model ? <span>{s.model}</span> : null}
          </div>
        </div>
        <div className="session-side">
          <span className="cost">{usd(s.costUsd, t("sessions.unmeasured"))}</span>
          <span className="muted" title={dateTime(s.lastEventAt)}>{age(s.lastEventAt, now, dict.time)}</span>
        </div>
      </Link>
      {mine && ((s.startedFromWeb && s.webSessionRef && !ended) || ended) ? (
        <div className="session-actions">
          {mine && s.startedFromWeb && s.webSessionRef && !ended ? <Button variant="ghost" busy={busy} disabled={stopSent} onClick={stop}>{t("sessions.stop")}</Button> : null}
          {mine && ended ? <Button variant="ghost" busy={busy} onClick={hide}>{t("sessions.hide")}</Button> : null}
        </div>
      ) : null}
      {stopSent && !ended ? <Notice>{t("sessions.stopSent")}</Notice> : null}
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
    </div>
  );
}

/**
 * Every machine in `machines` as a card (how many sessions run on it now); pressing a card opens its sessions under it, grouped by repository
 * (with `withEmpty` false, while a search or filter is on, only the machines with a match, and those start open). A machine with no session is
 * still listed, with its Claude state and (for its owner) the controls to start Claude there. The machines stay where they are (by name); inside a machine the most recently active
 * repository and session come first. While the pointer (or the keyboard focus) is on a machine's card its order is kept as it was when the pointer
 * arrived: the data in it goes on updating, but no row moves under the reader until they leave.
 */
export function SessionList({ ws, rows, machines, agents, userId, now, complete, withEmpty, onChanged }: {
  ws: string; rows: SessionRow[]; machines: MachineView[]; agents: AgentRow[]; userId: string | undefined; now: number; complete: boolean; withEmpty: boolean;
  onChanged: () => void;
}) {
  const { t } = useI18n();
  const base = useId();
  const [frozen, setFrozen] = useState<Readonly<Record<string, MachineOrder>>>({});
  // what the reader pressed; a machine they did not touch is open only while a search or filter is on
  const [pressed, setPressed] = useState<Readonly<Record<string, boolean>>>({});
  const groups = groupSessions(rows, machines, frozen, withEmpty);
  const views = new Map(machines.map((m) => [m.agentId, m]));
  const owners = new Map(agents.map((a) => [a.id, a]));
  const freeze = (machine: MachineGroup) => setFrozen((f) => (f[machine.key] ? f : { ...f, [machine.key]: orderOf(machine) }));
  const release = (key: string) => setFrozen((f) => {
    if (!f[key]) return f;
    const rest = { ...f };
    delete rest[key];
    return rest;
  });
  const anyOpen = groups.some((g) => pressed[g.key] ?? !withEmpty);
  return (
    <div className="session-groups">
      {groups.length > 0 && !anyOpen ? <p className="muted">{t("views.sessions.pick")}</p> : null}
      <div className="machine-grid">
        {groups.map((machine, index) => {
          const agent = machine.agentId ? owners.get(machine.agentId) : undefined;
          const view = machine.agentId ? views.get(machine.agentId) : undefined;
          const open = pressed[machine.key] ?? !withEmpty;
          const panelId = `${base}-machine-${index}`;
          return (
            <Fragment key={machine.key}>
              <SessionMachineCard hostname={machine.hostname} view={view} stats={machineStats(machine)} complete={complete} open={open} panelId={panelId}
                onToggle={() => setPressed((p) => ({ ...p, [machine.key]: !open }))} />
              {open ? (
                <section id={panelId} className="session-machine machine-detail" aria-label={machine.hostname}
                  onMouseEnter={() => freeze(machine)} onMouseLeave={() => release(machine.key)}
                  onFocus={() => freeze(machine)} onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget)) release(machine.key); }}>
                  <SessionMachineHead ws={ws} hostname={machine.hostname} view={view}
                    owned={agent !== undefined && agent.userId === userId} agentVersion={agent?.version ?? null} count={machine.count} onChanged={onChanged} />
                  {machine.repos.length === 0 ? <p className="muted small session-none">{complete ? t("sessions.noneOnMachine") : t("sessions.noneLoaded")}</p> : null}
                  {machine.repos.map((group) => (
                    <div key={group.repo ?? ""} className="session-branch">
                      <h3 className="session-branch-name"><Icon name="projects" /> {group.repo ?? <span className="muted">{t("sessions.noRepo")}</span>}</h3>
                      <ul className="session-list">
                        {group.rows.map((s) => <li key={s.id}><SessionItem ws={ws} s={s} now={now} mine={s.ownerId === userId} onChanged={onChanged} /></li>)}
                      </ul>
                    </div>
                  ))}
                </section>
              ) : null}
            </Fragment>
          );
        })}
      </div>
    </div>
  );
}
