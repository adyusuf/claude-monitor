import { useState } from "react";
import { Link } from "react-router";
import type { SessionRow } from "../api/types";
import { StatusPill } from "../components/ui";
import { useI18n } from "../i18n";
import { age, dateTime, usd } from "../lib/format";
import { groupSessions, type MachineGroup, type MachineOrder, orderOf } from "../lib/sessionGroups";

function SessionRowLink({ ws, s, now }: { ws: string; s: SessionRow; now: number }) {
  const { t, dict } = useI18n();
  return (
    <Link className="session-row" to={`/w/${ws}/sessions/${s.id}`}>
      <div className="session-main">
        <div className="session-title">
          <StatusPill status={s.status} />
          <span className="title-text">{s.title ?? t("sessions.untitled")}</span>
          {s.openPermissions > 0 ? <span className="badge warn">{t("sessions.permissionsWaiting", { n: s.openPermissions })}</span> : null}
        </div>
        <div className="session-meta muted">
          {s.projectName ? <span>▸ {s.projectName}</span> : null}
          <span>◍ {s.ownerName}</span>
          {s.model ? <span>{s.model}</span> : null}
        </div>
      </div>
      <div className="session-side">
        <span className="cost">{usd(s.costUsd, t("sessions.unmeasured"))}</span>
        <span className="muted" title={dateTime(s.lastEventAt)}>{age(s.lastEventAt, now, dict.time)}</span>
      </div>
    </Link>
  );
}

/**
 * Sessions under their machine and, within it, their branch. The machines stay where they are (by name); inside a machine the most recently
 * active branch and session come first. While the pointer (or the keyboard focus) is on a machine's card its order is kept as it was when
 * the pointer arrived: the data in it goes on updating, but no row moves under the reader until they leave.
 */
export function SessionList({ ws, rows, now }: { ws: string; rows: SessionRow[]; now: number }) {
  const { t } = useI18n();
  const [frozen, setFrozen] = useState<Readonly<Record<string, MachineOrder>>>({});
  const machines = groupSessions(rows, frozen);
  const freeze = (machine: MachineGroup) => setFrozen((f) => (f[machine.hostname] ? f : { ...f, [machine.hostname]: orderOf(machine) }));
  const release = (hostname: string) => setFrozen((f) => {
    if (!f[hostname]) return f;
    const rest = { ...f };
    delete rest[hostname];
    return rest;
  });
  return (
    <div className="session-groups">
      {machines.map((machine) => (
        <section key={machine.hostname} className="session-machine" aria-label={machine.hostname}
          onMouseEnter={() => freeze(machine)} onMouseLeave={() => release(machine.hostname)}
          onFocus={() => freeze(machine)} onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget)) release(machine.hostname); }}>
          <h2 className="session-machine-name"><span aria-hidden>⌂</span> {machine.hostname} <span className="badge">{machine.count}</span></h2>
          {machine.branches.map((group) => (
            <div key={group.branch ?? ""} className="session-branch">
              <h3 className="session-branch-name"><span aria-hidden>⎇</span> {group.branch ?? <span className="muted">{t("sessions.noBranch")}</span>}</h3>
              <ul className="session-list">
                {group.rows.map((s) => <li key={s.id}><SessionRowLink ws={ws} s={s} now={now} /></li>)}
              </ul>
            </div>
          ))}
        </section>
      ))}
    </div>
  );
}
