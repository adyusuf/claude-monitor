import { useCallback, useEffect, useState } from "react";
import { useParams } from "react-router";
import { api } from "../api/endpoints";
import type { ApprovalRequestRow, ApprovalRuleRow } from "../api/types";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { isCatchUp, useLive } from "../lib/live";
import { ApprovalItem } from "./ApprovalItem";

/** A standing approval: its machine, tool and command, how long it lasts and how often it answered; Revoke ends it. */
function RuleRow({ rule, onRevoked }: { rule: ApprovalRuleRow; onRevoked: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const revoke = async () => {
    if (!window.confirm(t("approvalRules.revokeConfirm"))) return;
    setBusy(true);
    setError(null);
    try {
      await api.revokeApprovalRule(rule.id);
      onRevoked();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <tr>
      <td>{rule.hostname}{rule.projectName ? <span className="muted small"> · {rule.projectName}</span> : null}</td>
      <td>{rule.toolName}</td>
      <td>{rule.command !== null ? <code>{rule.command}</code> : <span className="muted">{t("approvalRules.anyInput")}</span>}</td>
      <td>{t(`approvalRules.duration.${rule.duration}`)}</td>
      <td>{rule.expiresAt ? dateTime(rule.expiresAt) : <span className="muted">{t("approvalRules.never")}</span>}</td>
      <td className="num">{rule.useCount}</td>
      <td>
        <Button variant="ghost" busy={busy} onClick={() => void revoke()}>{t("approvalRules.revoke")}</Button>
        {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      </td>
    </tr>
  );
}

/**
 * The approvals page (ADR-0019 §12): the requests waiting on the viewer's sessions, the viewer's standing approvals with Revoke, and the latest
 * answers on their sessions (who or which standing approval answered). Re-read on every live change of a permission.
 */
export function ApprovalsPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [waiting, setWaiting] = useState<ApprovalRequestRow[] | null>(null);
  const [rules, setRules] = useState<ApprovalRuleRow[]>([]);
  const [recent, setRecent] = useState<ApprovalRequestRow[]>([]);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback(async () => {
    try {
      const [w, r, a] = await Promise.all([api.workspacePermissions(ws), api.approvalRules(ws), api.workspacePermissions(ws, "answered")]);
      setWaiting(w);
      setRules(r);
      setRecent(a);
      setError(null);
    } catch (e) {
      setError(e);
    }
  }, [ws]);
  useEffect(() => {
    void load();
  }, [load]);
  const live = useLive(ws, (m) => {
    if (m.event === "permission" || isCatchUp(m)) void load();
  });

  return (
    <div className="page">
      <header className="page-head">
        <h1>{t("approvalRules.title")}</h1>
        <span className={live ? "live on" : "live"}><span className="dot" aria-hidden />{t("sessions.live")}</span>
      </header>
      <p className="muted">{t("approvalRules.intro")}</p>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      <Card title={t("approvalRules.waiting")} className={waiting && waiting.length > 0 ? "card-attention" : undefined}>
        {waiting === null ? (error ? null : <Spinner />) : waiting.length === 0 ? <p className="muted">{t("approvalRules.noneWaiting")}</p>
          : waiting.map((r) => <ApprovalItem key={r.id} ws={ws} row={r} onDone={() => void load()} />)}
      </Card>
      <Card title={t("approvalRules.rules")}>
        {rules.length === 0 ? <p className="muted">{t("approvalRules.noRules")}</p> : (
          <div className="table-scroll">
            <table className="table table-rows">
              <thead><tr>
                <th>{t("approvalRules.machine")}</th><th>{t("approvalRules.tool")}</th><th>{t("approvalRules.command")}</th>
                <th>{t("approvalRules.remember")}</th><th>{t("approvalRules.ends")}</th><th className="num">{t("approvalRules.uses")}</th><th />
              </tr></thead>
              <tbody>{rules.map((r) => <RuleRow key={r.id} rule={r} onRevoked={() => void load()} />)}</tbody>
            </table>
          </div>
        )}
      </Card>
      <Card title={t("approvalRules.recent")}>
        {recent.length === 0 ? <p className="muted">{t("approvalRules.noRecent")}</p> : (
          <div className="table-scroll">
            <table className="table table-rows">
              <thead><tr>
                <th>{t("approvalRules.tool")}</th><th>{t("approvalRules.session")}</th><th>{t("approvalRules.machine")}</th>
                <th>{t("approvalRules.answeredBy")}</th><th>{t("approvalRules.when")}</th>
              </tr></thead>
              <tbody>{recent.map((r) => (
                <tr key={r.id}>
                  <td>{r.toolName} <span className={r.decision === "allow" ? "badge ok" : "badge"}>{r.decision ? t(`approvalRules.decision.${r.decision}`) : ""}</span></td>
                  <td>{r.sessionTitle ?? t("approvalRules.untitled")}</td>
                  <td>{r.hostname}</td>
                  <td>{r.answeredByRule ? t("approvalRules.byRule") : (r.answeredByName ?? "")}</td>
                  <td>{dateTime(r.answeredAt)}</td>
                </tr>
              ))}</tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}
