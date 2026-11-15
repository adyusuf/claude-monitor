import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { AuditRow } from "../api/types";
import { Button, Json, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";

function hasDetail(detail: unknown): boolean {
  if (detail === null || detail === undefined) return false;
  return typeof detail !== "object" || Object.keys(detail).length > 0;
}

/**
 * The audit log as a table: when, what, who did it, what it touched, and the recorded detail. It reads the newest page, then older
 * ones on request, and reads again from the newest whenever `version` changes (after a save).
 */
export function WorkspaceAudit({ ws, version }: { ws: string; version: number }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<AuditRow[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    let current = true;
    api.audit(ws).then((p) => {
      if (!current) return;
      setRows(p.items);
      setNext(p.next);
      setError(null);
    }).catch((e: unknown) => { if (current) setError(e); });
    return () => { current = false; };
  }, [ws, version]);

  const more = async () => {
    try {
      const p = await api.audit(ws, next);
      setRows((old) => [...(old ?? []), ...p.items]);
      setNext(p.next);
      setError(null);
    } catch (e) {
      setError(e);
    }
  };

  const alert = error ? <Notice kind="error">{errorText(error)}</Notice> : null;
  if (rows === null) return alert;
  if (rows.length === 0) return <>{alert}<p className="muted">{t("workspace.noAudit")}</p></>;
  return (
    <>
      {alert}
      <table className="table compact">
        <thead>
          <tr>
            <th>{t("views.workspace.colWhen")}</th><th>{t("views.workspace.colAction")}</th><th>{t("views.workspace.colActor")}</th>
            <th>{t("views.workspace.colTarget")}</th><th>{t("views.workspace.colDetail")}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((a) => (
            <tr key={a.id}>
              <td className="muted small">{dateTime(a.at)}</td>
              <td><code>{a.action}</code></td>
              <td>
                {a.actorUserId ? <><span className="muted small">{t("views.workspace.actorUser")}</span> <code>{a.actorUserId}</code></>
                  : a.actorAgentId ? <><span className="muted small">{t("views.workspace.actorAgent")}</span> <code>{a.actorAgentId}</code></> : "–"}
              </td>
              <td>{a.targetType ? <><span className="muted small">{a.targetType}</span> {a.targetId ? <code>{a.targetId}</code> : null}</> : "–"}</td>
              <td>
                {hasDetail(a.detail)
                  ? <details><summary className="link">{t("views.workspace.showDetail")}</summary><Json value={a.detail} /></details>
                  : "–"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {next ? <div className="more"><Button variant="ghost" onClick={() => void more()}>{t("sessions.more")}</Button></div> : null}
    </>
  );
}
