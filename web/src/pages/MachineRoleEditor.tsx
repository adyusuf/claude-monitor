import { useState } from "react";
import { api } from "../api/endpoints";
import type { MachineEnvironment, MachineFunction, MachineView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { isStrict, RoleBadge } from "./MachineBadges";
import { useCodeGate } from "./useCodeGate";

const ENVIRONMENTS: readonly MachineEnvironment[] = ["dev", "test", "stage", "preprod", "production", "other"];
const FUNCTIONS: readonly MachineFunction[] = ["development", "application", "web", "database", "ci", "storage", "other"];

/**
 * The role of a machine (ADR-0008 §3a): a workspace admin picks the environment and the function; anyone else reads them. Lowering
 * away from production or database answers 403 "reauth_required", which the code gate turns into the code field. The machine's own
 * claim is shown beside it and never edited here: it only tightens.
 */
export function MachineRoleEditor({ agentId, view: m, canEdit, onChanged }: { agentId: string; view: MachineView; canEdit: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const [environment, setEnvironment] = useState<string>(m.roleEnvironment ?? "");
  const [fn, setFn] = useState<string>(m.roleFunction ?? "");
  const [saved, setSaved] = useState(false);
  // A re-read machine (a live message, or the reload after a save) resets the lists to what is stored; the saved notice stays.
  const stored = `${m.roleEnvironment ?? ""}/${m.roleFunction ?? ""}`;
  const [seen, setSeen] = useState(stored);
  if (seen !== stored) {
    setSeen(stored);
    setEnvironment(m.roleEnvironment ?? "");
    setFn(m.roleFunction ?? "");
  }
  const gate = useCodeGate("reauth_required", "machines.role.lowerHint");
  const claim = m.claimedEnvironment || m.claimedFunction
    ? t("machines.role.claimed", { role: `${m.claimedEnvironment ?? t("machines.role.notSet")}/${m.claimedFunction ?? t("machines.role.notSet")}` }) : null;

  const save = async () => {
    setSaved(false);
    const ok = await gate.attempt((code) => api.setAgentRole(agentId, { environment: environment || null, function: fn || null, code }).then(() => undefined));
    if (ok) {
      setSaved(true);
      onChanged();
    }
  };

  return (
    <div className="stack">
      <p><RoleBadge view={m} />{isStrict(m) ? <> <span className="muted small">{t("machines.role.strict")}</span></> : null}</p>
      {claim ? <p className="muted small">{claim} {t("machines.role.claimTightens")}</p> : null}
      {canEdit ? (
        <form className="stack" onSubmit={(e) => {
          e.preventDefault();
          void save();
        }}>
          <div className="row">
            <label className="field">
              <span className="field-label">{t("machines.role.environment")}</span>
              <select className="input" value={environment} onChange={(e) => setEnvironment(e.target.value)}>
                <option value="">{t("machines.role.notSet")}</option>
                {ENVIRONMENTS.map((v) => <option key={v} value={v}>{v} · {t(`machines.role.env.${v}`)}</option>)}
              </select>
            </label>
            <label className="field">
              <span className="field-label">{t("machines.role.function")}</span>
              <select className="input" value={fn} onChange={(e) => setFn(e.target.value)}>
                <option value="">{t("machines.role.notSet")}</option>
                {FUNCTIONS.map((v) => <option key={v} value={v}>{v} · {t(`machines.role.fn.${v}`)}</option>)}
              </select>
            </label>
          </div>
          {gate.field}
          {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
          {saved ? <Notice kind="success">{t("machines.role.saved")}</Notice> : null}
          <div className="row-actions"><Button type="submit" busy={gate.busy}>{t("machines.role.save")}</Button></div>
        </form>
      ) : <p className="muted small">{t("machines.role.adminOnly")}</p>}
    </div>
  );
}
