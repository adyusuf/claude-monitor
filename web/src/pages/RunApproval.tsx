import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import { approvalKeys, payloadFor } from "../lib/approvalKey";
import type { WebRunView } from "../api/types";
import { Button, Card, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { countdown } from "../lib/format";
import { useNow } from "../lib/useNow";
import { RunFacts } from "./RunFacts";
import { useCodeGate } from "./useCodeGate";

/**
 * The owner's approval of one run (ADR-0005): the whole command, where it runs, who asked and why, and a countdown.
 * A shell run or an interpreter gets a red notice and an Allow button that waits a few seconds after the card appears.
 * There is no "allow all". Allow sends the run's hash; the API refuses it if the run changed. A shell run asks for a
 * two-step code when the owner has one.
 */
export function RunApproval({ run, ws, onChanged }: { run: WebRunView; ws: string; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const now = useNow(config.countdownTickMs);
  const isScript = run.mode === "script";
  const [armed, setArmed] = useState(!run.interpreter);
  const [read, setRead] = useState(!isScript); // a script must have been scrolled to its end before it can be allowed
  const [denying, setDenying] = useState(false);
  const [reason, setReason] = useState("");
  const [denyError, setDenyError] = useState<string | null>(null);
  const [signProblem, setSignProblem] = useState<string | null>(null);
  const gate = useCodeGate("mfa_required", "remote.codeHintRun", run.needsCode === true);
  const left = countdown(run.expiresAt, now);

  useEffect(() => {
    if (!run.interpreter) return;
    const timer = setTimeout(() => setArmed(true), config.runAllowDelayMs);
    return () => clearTimeout(timer);
  }, [run.interpreter]);

  const allow = async () => {
    if (!run.hash) return;
    const hash = run.hash;
    setSignProblem(null);
    // A signed approval (ADR-0010 §4): this browser's key signs the exact run; a machine that requires it refuses an approval without one.
    let proof: { keyId: string; signature: string } | null = null;
    try {
      proof = await approvalKeys.sign(payloadFor("run", run.id, run.targetAgentId, hash, run.expiresAt));
    } catch {
      proof = null;
    }

    if (run.approvalsRequired && !proof) return setSignProblem(t("approvals.needKey"));
    if (await gate.attempt((code) => api.approveRun(run.id, hash, code, proof ?? undefined))) onChanged();
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
      {isScript ? <div className="notice notice-error" role="alert">{run.strict ? t("remote.scriptBannerStrict", { role: run.targetFunction === "database" ? t("remote.scriptDatabase") : t("remote.scriptProduction") }) : t("remote.scriptBanner")}</div>
        : run.interpreter ? <div className="notice notice-error" role="alert">{run.mode === "shell" ? t("remote.interpreterShell") : t("remote.interpreterArgv")}</div> : null}
      {run.needsCode ? <p className="muted small">{t("remote.scriptNeedsCode")}</p> : null}
      {run.recentOutput ? <div className="notice notice-warn" role="alert">{t("remote.recentOutput")}</div> : null}
      <RunFacts run={run} ws={ws} onScriptEnd={() => setRead(true)} />
      {gate.field}
      {run.approvalsRequired ? <p className="muted small">{t("approvals.runNeedsKey")}</p> : null}
      {signProblem ? <Notice kind="error">{signProblem}</Notice> : null}
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
          {!read ? <span className="muted small">{t("remote.scriptScroll")}</span> : !armed ? <span className="muted small">{t("remote.allowWait")}</span> : null}
          <Button variant="danger" onClick={() => setDenying(true)}>{t("remote.deny")}</Button>
          <Button busy={gate.busy} disabled={gate.busy || !armed || !read || !gate.ready || !run.hash || !left} onClick={() => void allow()}>{t("remote.allow")}</Button>
        </div>
      )}
    </Card>
  );
}
