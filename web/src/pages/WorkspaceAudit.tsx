import type { AuditRow } from "../api/types";
import { Json } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";

function hasDetail(detail: unknown): boolean {
  if (detail === null || detail === undefined) return false;
  return typeof detail !== "object" || Object.keys(detail).length > 0;
}

/** The audit log as a table: when, what, who did it, what it touched, and the recorded detail. */
export function WorkspaceAudit({ rows }: { rows: AuditRow[] }) {
  const { t } = useI18n();
  if (rows.length === 0) return <p className="muted">{t("workspace.noAudit")}</p>;
  return (
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
  );
}
