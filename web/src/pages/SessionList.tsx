import { Link } from "react-router";
import type { SessionRow } from "../api/types";
import { StatusPill } from "../components/ui";
import { useI18n } from "../i18n";
import { age, dateTime, usd } from "../lib/format";
import { groupSessions } from "../lib/sessionGroups";

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

/** Sessions under their machine and, within it, their branch; the most recently active machine, branch and session come first. */
export function SessionList({ ws, rows, now }: { ws: string; rows: SessionRow[]; now: number }) {
  const { t } = useI18n();
  return (
    <div className="session-groups">
      {groupSessions(rows).map((machine) => (
        <section key={machine.hostname} className="session-machine" aria-label={machine.hostname}>
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
