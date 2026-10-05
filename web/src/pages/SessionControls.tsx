import { useCallback, useEffect, useState, type ReactNode } from "react";
import { api } from "../api/endpoints";
import type { CommandRow, PermissionRow } from "../api/types";
import { Button, Card, Json, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { time } from "../lib/format";
import { useNow } from "../lib/useNow";
import { CommandItem, isIdle } from "./CommandHistory";
import { PlanPrompt, PLAN_TOOL, QUESTION_TOOL, QuestionPrompt } from "./PermissionPrompts";

interface Props { sessionId: string; version: number; onChange: () => void }

/** The permissions' frame: a card of its own, or a section inside the conversation. */
function PermissionsFrame({ inline, children }: { inline: boolean; children: ReactNode }) {
  const { t } = useI18n();
  return inline
    ? <div className="chat-permissions"><h3 className="sub">{t("session.permissions")}</h3>{children}</div>
    : <Card title={t("session.permissions")} className="card-attention">{children}</Card>;
}

/** Tool calls waiting for permission: the session's owner allows or denies them from here. */
export function PermissionsCard({ sessionId, canAnswer, version, onChange, inline }: Props & { canAnswer: boolean; inline?: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<PermissionRow[]>([]);
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(() => api.permissions(sessionId, "open").then(setRows).catch(setError), [sessionId]);
  useEffect(() => {
    void load();
  }, [load, version]);

  if (rows.length === 0) return null;
  const answer = async (id: string, decision: "allow" | "deny", answers?: Record<string, string>) => {
    try {
      await api.answer(id, decision, reasons[id]?.trim() || undefined, answers);
      setRows((r) => r.filter((p) => p.id !== id));
      onChange();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <PermissionsFrame inline={inline === true}>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {rows.map((p) => (
        <div key={p.id} className="permission">
          <div className="permission-head">
            <strong>{p.toolName}</strong>
            <span className="muted small">{t("session.expires")}: {time(p.expiresAt)}</span>
          </div>
          {p.toolName === PLAN_TOOL ? <PlanPrompt input={p.toolInput} /> : p.toolName === QUESTION_TOOL ? null : <Json value={p.toolInput} />}
          {canAnswer && p.toolName === QUESTION_TOOL ? <QuestionPrompt input={p.toolInput} onAnswer={(answers) => void answer(p.id, "allow", answers)} /> : null}
          {canAnswer ? (
            <div className="permission-actions">
              <input className="input" placeholder={t("session.reason")} maxLength={config.reasonMax} value={reasons[p.id] ?? ""}
                onChange={(e) => setReasons({ ...reasons, [p.id]: e.target.value })} />
              {p.toolName === QUESTION_TOOL ? null : <Button onClick={() => void answer(p.id, "allow")}>{p.toolName === PLAN_TOOL ? t("chat.approvePlan") : t("session.allow")}</Button>}
              <Button variant="danger" onClick={() => void answer(p.id, "deny")}>{p.toolName === PLAN_TOOL ? t("chat.rejectPlan") : t("session.deny")}</Button>
            </div>
          ) : <p className="muted small">{t("session.onlyOwner")}</p>}
        </div>
      ))}
    </PermissionsFrame>
  );
}

/** Send a prompt or a stop to the session, and what was sent before. */
export function CommandsCard({ sessionId, canCommand, ended, lastEventAt, version, onChange }: Props & { canCommand: boolean; ended: boolean; lastEventAt: string }) {
  const { t } = useI18n();
  const now = useNow(config.clockTickMs);
  const errorText = useErrorText();
  const [rows, setRows] = useState<CommandRow[]>([]);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);

  const load = useCallback(() => api.commands(sessionId).then(setRows).catch(() => setRows([])), [sessionId]);
  useEffect(() => {
    void load();
  }, [load, version]);

  const send = async (kind: "prompt" | "stop") => {
    if (kind === "prompt" && !text.trim()) return setNotice({ kind: "error", text: t("errors.invalid_body") });
    if (kind === "stop" && !window.confirm(t("session.stopConfirm"))) return;
    setBusy(true);
    try {
      await api.sendCommand(sessionId, kind, kind === "prompt" ? text : undefined);
      setText("");
      setNotice({ kind: "success", text: t("session.sent") });
      await load();
      onChange();
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    } finally {
      setBusy(false);
    }
  };

  const cancel = async (id: string) => {
    try {
      await api.cancelCommand(id);
      await load();
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    }
  };

  return (
    <Card title={t("session.commands")}>
      {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
      {canCommand && !ended ? (
        <div className="compose">
          <textarea className="input" rows={3} maxLength={config.promptMax} placeholder={t("session.promptPlaceholder")}
            value={text} onChange={(e) => setText(e.target.value)} />
          {isIdle(lastEventAt, now) ? <Notice kind="info">{t("session.idleWarning")}</Notice> : null}
          <div className="compose-actions">
            <Button variant="danger" busy={busy} onClick={() => void send("stop")}>{t("session.stop")}</Button>
            <Button busy={busy} onClick={() => void send("prompt")}>{t("session.send")}</Button>
          </div>
        </div>
      ) : <p className="muted">{ended ? t("errors.session_ended") : t("session.onlyOwner")}</p>}
      <h3 className="sub">{t("session.history")}</h3>
      {rows.length === 0 ? <p className="muted">{t("session.noCommands")}</p> : (
        <ul className="plain commands">
          {rows.map((c) => <CommandItem key={c.id} command={c} now={now} canCancel={canCommand} onCancel={(id) => void cancel(id)} />)}
        </ul>
      )}
    </Card>
  );
}
