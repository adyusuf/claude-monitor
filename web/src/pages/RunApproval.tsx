import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { WebRunView } from "../api/types";
import { Button, Card, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { countdown } from "../lib/format";
import { useNow } from "../lib/useNow";
import { RunFacts } from "./RunFacts";
import { useCodeGate } from "./useCodeGate";

/**
 * The owner's approval of one run (ADR-0004): the whole command, where it runs, who asked and why, and a countdown.
 * A shell run or an interpreter gets a red notice and an Allow button that waits a few seconds after the card appears.
 * There is no "allow all". Allow sends the run's hash; the API refuses it if the run changed. A shell run asks for a
 * two-step code when the owner has one.
 */
export function RunApproval({ run, ws, onChanged }: { run: WebRunView; ws: string; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const now = useNow(config.countdownTickMs);
  const [armed, setArmed] = useState(!run.interpreter);
  const [denying, setDenying] = useState(false);
  const [reason, setReason] = useState("");
  const [denyError, setDenyError] = useState<string | null>(null);
  const gate = useCodeGate("mfa_required", "remote.codeHintRun");
  const left = countdown(run.expiresAt, now);

  useEffect(() => {
    if (!run.interpreter) return;
    const timer = setTimeout(() => setArmed(true), config.runAllowDelayMs);
    return () => clearTimeout(timer);
  }, [run.interpreter]);

  const allow = async () => {
    if (!run.hash) return;
    const hash = run.hash;
    if (await gate.attempt((code) => api.approveRun(run.id, hash, code))) onChanged();
  };

  const deny = async () => {
    setDenyError(null);
    try {
      await api.denyRun(run.id, reason.trim() || undefined);
      onChanged();
    } catch (e) {
      setDenyError(errorText(e));
    }
  };

  return (
    <Card className="card-attention run-approval" title={t("remote.approvalTitle", { host: run.targetHostname })}
      actions={<span className="muted small">{left ? t("remote.timeLeft", { time: left }) : t("remote.timeUp")}</span>}>
      {run.interpreter ? <div className="notice notice-error" role="alert">{run.mode === "shell" ? t("remote.interpreterShell") : t("remote.interpreterArgv")}</div> : null}
      {run.recentOutput ? <div className="notice notice-warn" role="alert">{t("remote.recentOutput")}</div> : null}
      <RunFacts run={run} ws={ws} />
      {gate.field}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      {denyError ? <Notice kind="error">{denyError}</Notice> : null}
      {denying ? (
        <div className="permission-actions">
          <input className="input" aria-label={t("remote.denyReason")} placeholder={t("remote.denyReason")} maxLength={config.reasonMax} value={reason}
            onChange={(e) => setReason(e.target.value)} />
          <Button variant="ghost" onClick={() => setDenying(false)}>{t("common.cancel")}</Button>
          <Button variant="danger" onClick={() => void deny()}>{t("remote.deny")}</Button>
        </div>
      ) : (
        <div className="permission-actions">
          {!armed ? <span className="muted small">{t("remote.allowWait")}</span> : null}
          <Button variant="danger" onClick={() => setDenying(true)}>{t("remote.deny")}</Button>
          <Button busy={gate.busy} disabled={gate.busy || !armed || !run.hash || !left} onClick={() => void allow()}>{t("remote.allow")}</Button>
        </div>
      )}
    </Card>
  );
}
