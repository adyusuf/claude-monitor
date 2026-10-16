import { Link } from "react-router";
import type { WebRunView } from "../api/types";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";
import { hasInvisible } from "../lib/visible";
import { RunCommand } from "./RunCommand";

/** The reason a requester's Claude gave: its own words, untrusted, so anything invisible in it is shown too. */
export function UntrustedReason({ text }: { text: string | null }) {
  const { t } = useI18n();
  return (
    <>
      <span className="badge warn">{t("remote.reasonUntrusted")}</span>
      {text ? <blockquote className="run-reason">{hasInvisible(text) ? <VisibleText text={text} /> : text}</blockquote>
        : <span className="muted"> {t("remote.reasonNone")}</span>}
    </>
  );
}

/** The role the approval card was made against: the environment and function as the API froze them, red when strict. */
function RoleOfRun({ run }: { run: WebRunView }) {
  const { t } = useI18n();
  const code = `${run.targetEnvironment ?? t("remote.scriptRoleNotSet")}/${run.targetFunction ?? t("remote.scriptRoleNotSet")}`;
  return <span className={run.strict ? "badge status-failed" : "badge"}>{code}</span>;
}

/** Everything the owner must see before allowing a run (ADR-0005, "The owner's approval"). */
export function RunFacts({ run, ws, onScriptEnd }: { run: WebRunView; ws: string; onScriptEnd?: () => void }) {
  const { t } = useI18n();
  return (
    <dl className="facts">
      <dt>{t("remote.target")}</dt>
      <dd><strong>{run.targetHostname}</strong> <span className="muted small">{t("remote.agent")} <code>{run.targetAgentId}</code></span></dd>
      <dt>{t("remote.mode")}</dt>
      <dd><span className={`badge run-mode-${run.mode}`}>{run.mode === "script" ? t("remote.modeScript") : run.mode === "shell" ? t("remote.modeShell") : t("remote.modeArgv")}</span></dd>
      {run.mode === "script" ? <><dt>{t("remote.scriptRole")}</dt><dd><RoleOfRun run={run} /></dd></> : null}
      <dt>{t("remote.command")}</dt>
      <dd><RunCommand run={run} onScriptEnd={onScriptEnd} /></dd>
      {run.mode === "script" ? null : <><dt>{t("remote.cwd")}</dt>
      <dd>{run.cwd ? <code className="shell-inline"><VisibleText text={run.cwd} /></code> : <span className="muted">{t("remote.cwdNone")}</span>}</dd></>}
      {run.projectKey || run.kind ? <><dt>{t("remote.project")}</dt><dd>{run.projectKey ? <code>{run.projectKey}</code> : <span className="muted">{t("remote.kindNone")}</span>}{run.kind ? <> · <span className="badge">{run.kind}</span></> : null}</dd></> : null}
      <dt>{t("remote.timeout")}</dt>
      <dd>{t("remote.seconds", { n: run.timeoutSeconds })}</dd>
      <dt>{t("remote.grant")}</dt>
      <dd>{run.grantId ? <code>{run.grantId}</code> : <span className="muted">{t("remote.grantNone")}</span>}{run.jobId ? <> · {t("remote.job")} <code>{run.jobId}</code></> : null}</dd>
      <dt>{t("remote.requester")}</dt>
      <dd>
        {run.requesterUser} · {run.requesterHostname}
        {run.requesterSessionId ? <> · <Link to={`/w/${ws}/sessions/${run.requesterSessionId}`}>{t("remote.session")}</Link></> : null}
        {run.selfApproval ? <div className="muted small">{t("remote.selfApproval")}</div> : null}
      </dd>
      <dt>{t("remote.reason")}</dt>
      <dd><UntrustedReason text={run.reason} /></dd>
    </dl>
  );
}
