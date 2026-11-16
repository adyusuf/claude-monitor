import { api } from "../api/endpoints";
import type { WebJobView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { useI18n, type Key } from "../i18n";
import { dateTime } from "../lib/format";
import { RunCommand } from "./RunCommand";
import { UntrustedReason } from "./RunFacts";
import { useCodeGate } from "./useCodeGate";

/** One job: a named command Claude proposed to run again and again; its command is frozen, so approving it approves exactly this. */
function JobRow({ job: j, onChanged }: { job: WebJobView; onChanged: () => void }) {
  const { t, tx } = useI18n();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const act = async (call: (code?: string) => Promise<void>) => {
    if (await gate.attempt(call)) onChanged();
  };
  return (
    <li className="grant-row">
      <div className="run-line">
        <span className={`badge job-${j.status}`}>{tx(`remote.jobStatus.${j.status}`)}</span>
        <strong><VisibleText text={j.name} /></strong>
        <span className="muted small">{t("remote.proposedBy", { user: j.proposedByName })}{j.proposedByHostname ? ` · ${j.proposedByHostname}` : ""}</span>
        <span className="muted small">{dateTime(j.createdAt)}</span>
      </div>
      <RunCommand run={{ visible: true, mode: "argv", argv: j.argv, shellCommand: null }} />
      <dl className="facts">
        <dt>{t("remote.cwd")}</dt>
        <dd><code className="shell-inline"><VisibleText text={j.cwd} /></code></dd>
        <dt>{t("remote.timeout")}</dt>
        <dd>{t("remote.seconds", { n: j.timeoutSeconds })}</dd>
        {j.reason ? <><dt>{t("remote.reason")}</dt><dd><UntrustedReason text={j.reason} /></dd></> : null}
      </dl>
      {gate.field}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      {j.canDecide || j.canRetire ? (
        <div className="permission-actions">
          {j.canDecide ? <Button variant="danger" busy={gate.busy} onClick={() => void act(() => api.denyJob(j.id))}>{t("remote.deny")}</Button> : null}
          {j.canDecide ? <Button busy={gate.busy} onClick={() => void act((code) => api.approveJob(j.id, code))}>{t("remote.approve")}</Button> : null}
          {j.canRetire ? <Button variant="ghost" busy={gate.busy} onClick={() => void act(() => api.retireJob(j.id))}>{t("remote.retire")}</Button> : null}
        </div>
      ) : null}
    </li>
  );
}

/** The job list's filters, sent as the API's `status` (empty: every job; `open` and `finished` are its groups). */
export const JOB_FILTERS = ["", "open", "finished"] as const;
export type JobFilter = (typeof JOB_FILTERS)[number];

const filterKey = (f: JobFilter): Key => {
  switch (f) {
    case "open":
      return "remote.jobFilter.open";
    case "finished":
      return "remote.jobFilter.finished";
    default:
      return "remote.jobFilter.all";
  }
};

/** A machine's jobs: proposed ones wait for the owner, active ones can be run by name. The filter keeps an older job reachable once there are many. */
export function JobList({ jobs, filter = "", onFilter, hasMore = false, onMore, onChanged }: {
  jobs: WebJobView[];
  /** The status filter the jobs were read with, and its change (the page reads again). */
  filter?: JobFilter; onFilter?: (filter: JobFilter) => void;
  /** True when a full page came back: older jobs can be read with onMore. */
  hasMore?: boolean; onMore?: () => void; onChanged: () => void;
}) {
  const { t } = useI18n();
  return (
    <>
      {onFilter ? (
        <label className="field">
          <span>{t("remote.jobFilter.label")}</span>
          <select className="input" value={filter} onChange={(e) => onFilter(e.target.value as JobFilter)}>
            {JOB_FILTERS.map((f) => <option key={f} value={f}>{t(filterKey(f))}</option>)}
          </select>
        </label>
      ) : null}
      {jobs.length === 0 ? <p className="muted">{t("remote.noJobs")}</p> : <ul className="plain run-list">{jobs.map((j) => <JobRow key={j.id} job={j} onChanged={onChanged} />)}</ul>}
      {hasMore && onMore ? <div className="more"><Button variant="ghost" onClick={onMore}>{t("sessions.more")}</Button></div> : null}
    </>
  );
}
