import { useState } from "react";
import { api } from "../api/endpoints";
import { Button, Field, Notice } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { useCodeGate } from "./useCodeGate";

/** The owner gives a grant on their own machine: one command template, a folder, a time limit and a number of days. */
export function GrantForm({ agentId, onCreated }: { agentId: string; onCreated: () => void }) {
  const { t } = useI18n();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const [template, setTemplate] = useState("");
  const [cwd, setCwd] = useState("");
  const [timeout, setTimeoutSeconds] = useState(60);
  const [days, setDays] = useState<number>(config.grantDaysDefault);
  const [reason, setReason] = useState("");
  const [invalid, setInvalid] = useState<string | null>(null);

  const elements = template.split("\n").map((line) => line.replace(/\r$/, "")).filter((line) => line !== "");
  const submit = async () => {
    if (elements.length === 0 || !cwd.trim()) return setInvalid(t("remote.grantIncomplete"));
    if (!(timeout >= 1 && timeout <= config.grantTimeoutMax) || !(days >= 1 && days <= config.grantDaysMax)) return setInvalid(t("errors.out_of_range"));
    setInvalid(null);
    const input = { template: elements, cwd: cwd.trim(), maxTimeoutSeconds: timeout, days, reason: reason.trim() || undefined };
    if (await gate.attempt((code) => api.createGrant(agentId, { ...input, code }).then(() => undefined))) {
      setTemplate("");
      setReason("");
      onCreated();
    }
  };

  return (
    <div className="stack grant-form">
      <h3 className="sub">{t("remote.grantNew")}</h3>
      {invalid ? <Notice kind="error">{invalid}</Notice> : null}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      <label className="field">
        <span className="field-label">{t("remote.grantTemplate")}</span>
        <textarea className="input" rows={4} spellCheck={false} value={template} onChange={(e) => setTemplate(e.target.value)} />
        <span className="field-hint">{t("remote.grantTemplateHint")}</span>
      </label>
      <Field label={t("remote.cwd")} value={cwd} onChange={(e) => setCwd(e.target.value)} />
      <div className="inline-form">
        <Field label={t("remote.maxTimeout")} type="number" min={1} max={config.grantTimeoutMax} value={timeout} onChange={(e) => setTimeoutSeconds(Number(e.target.value))} />
        <Field label={t("remote.days")} type="number" min={1} max={config.grantDaysMax} value={days} onChange={(e) => setDays(Number(e.target.value))} />
      </div>
      <Field label={t("remote.reasonOptional")} value={reason} maxLength={config.reasonMax} onChange={(e) => setReason(e.target.value)} />
      {gate.field}
      <div><Button busy={gate.busy} onClick={() => void submit()}>{t("remote.grantCreate")}</Button></div>
    </div>
  );
}
