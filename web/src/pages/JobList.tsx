import { api } from "../api/endpoints";
import type { WebJobView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";
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

/** A machine's jobs: proposed ones wait for the owner, active ones can be run by name. */
export function JobList({ jobs, onChanged }: { jobs: WebJobView[]; onChanged: () => void }) {
  const { t } = useI18n();
  if (jobs.length === 0) return <p className="muted">{t("remote.noJobs")}</p>;
  return <ul className="plain run-list">{jobs.map((j) => <JobRow key={j.id} job={j} onChanged={onChanged} />)}</ul>;
}
