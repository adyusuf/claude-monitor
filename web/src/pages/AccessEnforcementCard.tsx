import { useState } from "react";
import { api } from "../api/endpoints";
import { Button, Card, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { useCodeGate } from "./useCodeGate";

/**
 * The workspace switch "enforce access policies" (ADR-0020 §4 rule 4). Off: a run whose repository has a policy is held to it, any other run is as
 * before. On: a run in a repository without a policy, or that names none, gets the default (the project's folder, programs only, no shell or script);
 * production and database machines always do. Owners and admins change it, with the code.
 */
export function AccessEnforcementCard({ ws, enforce, canEdit, onChanged }: { ws: string; enforce: boolean; canEdit: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const gate = useCodeGate("reauth_required", "access.codeHint");
  const [on, setOn] = useState(enforce);
  const [saved, setSaved] = useState(false);

  const save = async () => {
    setSaved(false);
    if (await gate.attempt((code) => api.saveAccessEnforcement(ws, on, code))) {
      setSaved(true);
      onChanged();
    }
  };

  return (
    <Card>
      <div className="stack">
        {canEdit ? null : <p className="muted small">{t("remote.settingsReadOnly")}</p>}
        <label className="check">
          <input type="checkbox" role="switch" checked={on} disabled={!canEdit} onChange={(e) => { setSaved(false); setOn(e.target.checked); }} />
          <span>{t("access.enforce")}<span className="field-hint">{t("access.enforceHint")}</span></span>
        </label>
        <ul className="bullets small">
          <li>{t("access.enforceOff")}</li>
          <li>{t("access.enforceOn")}</li>
          <li>{t("access.enforceStrict")}</li>
        </ul>
        {gate.field}
        {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
        {saved ? <Notice kind="success">{t("workspace.saved")}</Notice> : null}
        {canEdit ? <div><Button busy={gate.busy} disabled={on === enforce || !gate.ready} onClick={() => void save()}>{t("workspace.save")}</Button></div> : null}
      </div>
    </Card>
  );
}
