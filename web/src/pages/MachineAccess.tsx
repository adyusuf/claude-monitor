import { Link } from "react-router";
import { ACCESS_STATES, type MachineAccessPolicyView, type MachineAccessView } from "../api/typesAccess";
import { Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { AccessLists } from "./AccessLists";

// The states in which the machine holds the web's policy; in every other one the default applies to that project's runs.
const HELD: readonly string[] = ["applied", "kept_signed"];

/** A policy's state on the machine as a badge: green while the machine holds it, amber when the default applies instead. */
export function AccessStateBadge({ state }: { state: string | null }) {
  const { t, tx } = useI18n();
  if (!state) return <span className="badge warn">{t("access.notReceived")}</span>;
  const known = (ACCESS_STATES as readonly string[]).includes(state);
  return <span className={HELD.includes(state) ? "badge ok" : "badge warn"}>{known ? tx(`access.state.${state}`) : state}</span>;
}

/** One project's policy on this machine: the web's version, the one applied here and in which state, and the effective lists for the machine's OS. */
function PolicyRow({ ws, policy }: { ws: string; policy: MachineAccessPolicyView }) {
  const { t } = useI18n();
  const behind = policy.appliedVersion !== null && policy.appliedVersion < policy.webVersion;
  return (
    <li className="stack-tight">
      <div className="row small">
        <strong><Link to={`/w/${ws}/projects/${policy.projectId}`}>{policy.projectName}</Link></strong>
        <span className="badge">{t("access.webVersion", { version: policy.webVersion })}</span>
        <span className={behind ? "badge warn" : "badge"}>
          {policy.appliedVersion === null ? t("access.appliedNone") : t("access.appliedVersion", { version: policy.appliedVersion })}
        </span>
        <AccessStateBadge state={policy.state} />
        {policy.isSigned ? <span className="badge ok">{t("access.signed")}</span> : <span className="badge warn">{t("access.unsigned")}</span>}
        {policy.shell ? <span className="badge status-failed">{t("access.shellOn")}</span> : null}
      </div>
      {policy.includeProjectFolder ? <p className="muted small">{t("access.projectFolderIncluded")}</p> : null}
      <AccessLists allowed={policy.allowedFolders} denied={policy.deniedFolders} applications={policy.applications} />
    </li>
  );
}

/**
 * A machine's access (ADR-0020 §4), read-only: the machine's own ceiling (only for its owner and the workspace's admins: the API leaves it out for
 * anyone else), whether runs without a policy get the default here, and each project policy with what this machine made of it. An agent that sent
 * no access report is too old to apply policies.
 */
export function MachineAccess({ ws, access }: { ws: string; access: MachineAccessView }) {
  const { t } = useI18n();
  if (access.reportedAt === null) return <Notice>{t("access.agentTooOld")}</Notice>;
  return (
    <div className="stack">
      <p className="muted small">{t("access.machineIntro")} {t("access.reportedAt", { at: dateTime(access.reportedAt) })}</p>
      <p>{access.defaultApplies ? t("access.defaultAppliesOn") : t("access.defaultAppliesOff")}</p>
      {access.ceiling ? (
        <section className="stack-tight" aria-label={t("access.ceiling")}>
          <h3 className="subhead">{t("access.ceiling")}</h3>
          <p className="muted small">{t("access.ceilingHint")}</p>
          <AccessLists allowed={access.ceiling.allowedFolders} denied={access.ceiling.deniedFolders} applications={access.ceiling.applications} />
          <p className="small">{access.ceiling.shell ? t("access.ceilingShellOn") : t("access.ceilingShellOff")}</p>
        </section>
      ) : <p className="muted small">{t("access.ceilingHidden")}</p>}
      <section className="stack-tight" aria-label={t("access.policies")}>
        <h3 className="subhead">{t("access.policies")}</h3>
        {access.policies.length === 0 ? <p className="muted">{t("access.noPolicies")}</p> : (
          <ul className="plain">{access.policies.map((p) => <PolicyRow key={p.projectId} ws={ws} policy={p} />)}</ul>
        )}
      </section>
    </div>
  );
}
