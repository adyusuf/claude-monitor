import { useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import { APPROVAL_DURATIONS, type ApprovalDuration, type ApprovalRequestRow } from "../api/types";
import { Icon } from "../components/icons";
import { Button, Json, Notice } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { time } from "../lib/format";
import { PlanPrompt, PLAN_TOOL, QUESTION_TOOL } from "./PermissionPrompts";
import { useCodeGate } from "./useCodeGate";

/** The command of a Bash request, the one thing a standing approval for it covers; null for any other tool. */
export function commandOf(row: { toolName: string; toolInput: Record<string, unknown> }): string | null {
  const c = row.toolInput.command;
  return row.toolName === "Bash" && typeof c === "string" ? c : null;
}

/** The keys of a Bash call other than its command and description (a sandbox flag, background, …), so the owner sees all that runs. */
export function bashExtras(row: { toolName: string; toolInput: Record<string, unknown> }): Record<string, unknown> | null {
  if (commandOf(row) === null) return null;
  const rest = Object.fromEntries(Object.entries(row.toolInput).filter(([k]) => k !== "command" && k !== "description"));
  return Object.keys(rest).length > 0 ? rest : null;
}

/** A one-line preview of a request: the command, a file path, or the tool alone. */
export function previewOf(row: { toolName: string; toolInput: Record<string, unknown> }): string {
  const command = commandOf(row);
  if (command !== null) return command;
  for (const key of ["file_path", "path", "url", "pattern", "query"]) {
    const v = row.toolInput[key];
    if (typeof v === "string" && v) return v;
  }
  return "";
}

/**
 * One permission request waiting on the viewer's session (ADR-0019): where it comes from, what it asks, and Allow / Deny with how long the allow
 * stands. A question is answered on its session page (it needs the chosen options); a strict machine's request is decided once.
 */
export function ApprovalItem({ ws, row, onDone }: { ws: string; row: ApprovalRequestRow; onDone: (id: string) => void }) {
  const { t } = useI18n();
  const [remember, setRemember] = useState<ApprovalDuration>("once");
  const [reason, setReason] = useState("");
  const gate = useCodeGate("reauth_required", "approvalRules.rememberHintAlways");
  const question = row.toolName === QUESTION_TOOL;
  const command = commandOf(row);
  const extras = bashExtras(row);

  const answer = async (decision: "allow" | "deny") => {
    const always = decision === "allow" && remember === "always";
    const ok = await gate.attempt((code) => api.answer(row.id, decision, reason.trim() || undefined, undefined,
      { remember: decision === "allow" && remember !== "once" ? remember : undefined, code: always ? code : undefined }));
    if (ok) onDone(row.id);
  };

  return (
    <div className="permission approval-item">
      <div className="permission-head">
        <strong>{row.toolName}</strong>
        <span className="muted small">{t("approvalRules.expires")}: {time(row.expiresAt)}</span>
      </div>
      <p className="small approval-where">
        <Link to={`/w/${ws}/sessions/${row.sessionId}`}>{row.sessionTitle ?? t("approvalRules.untitled")}</Link>
        <span className="muted"> · <Icon name="machines" /> {row.hostname}</span>
        {row.projectName ? <span className="muted"> · <Icon name="projects" /> {row.projectName}</span> : null}
        {row.strict ? <span className="badge warn">{t("machines.role.strict")}</span> : null}
      </p>
      {command !== null ? <><pre className="code approval-command">{command}</pre>{extras ? <Json value={extras} /> : null}</>
        : row.toolName === PLAN_TOOL ? <PlanPrompt input={row.toolInput} /> : question ? null : <Json value={row.toolInput} />}
      {question ? (
        <p className="small">{t("approvalRules.questionOnSession")} <Link to={`/w/${ws}/sessions/${row.sessionId}`}>{t("approvalRules.openSession")}</Link></p>
      ) : (
        <>
          {row.canRemember ? (
            <label className="field approval-remember">
              <span className="field-label">{t("approvalRules.remember")}</span>
              <select className="input" value={remember} onChange={(e) => setRemember(e.target.value as ApprovalDuration)}>
                {APPROVAL_DURATIONS.map((d) => <option key={d} value={d}>{t(`approvalRules.duration.${d}`)}</option>)}
              </select>
              <span className="muted small">{remember === "always" ? t("approvalRules.rememberHintAlways") : t("approvalRules.rememberHint")}</span>
            </label>
          ) : <p className="muted small">{row.strict ? t("approvalRules.strictNoRemember") : t("approvalRules.noRemember")}</p>}
          {remember === "always" ? gate.field : null}
          {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
          <div className="permission-actions">
            <input className="input" aria-label={t("approvalRules.reason")} placeholder={t("approvalRules.reason")} maxLength={config.reasonMax}
              value={reason} onChange={(e) => setReason(e.target.value)} />
            <Button busy={gate.busy} disabled={remember === "always" && !gate.ready} onClick={() => void answer("allow")}>{row.toolName === PLAN_TOOL ? t("chat.approvePlan") : t("approvalRules.allow")}</Button>
            <Button variant="danger" busy={gate.busy} onClick={() => void answer("deny")}>{row.toolName === PLAN_TOOL ? t("chat.rejectPlan") : t("approvalRules.deny")}</Button>
          </div>
        </>
      )}
    </div>
  );
}
