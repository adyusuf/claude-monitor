import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ProjectMemberView, ProjectRole } from "../api/typesProjects";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { PROJECT_ROLES } from "../lib/projectOptions";

/** Who works on a project and in which role. Everyone reads the list; a project manager changes it. */
export function ProjectMembers({ projectId, workspaceMembers, canEdit }: { projectId: string; workspaceMembers: Member[]; canEdit: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<ProjectMemberView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [person, setPerson] = useState("");
  const [role, setRole] = useState<ProjectRole>("contributor");

  const load = useCallback(async () => {
    try {
      setRows(await api.projectMembers(projectId));
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (work: () => Promise<unknown>) => {
    try {
      setError(null);
      await work();
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const candidates = workspaceMembers.filter((m) => !rows?.some((r) => r.userId === m.userId));
  const chosen = person || candidates[0]?.userId || "";

  return (
    <Card title={t("projects.members")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <p className="muted">{t("projects.noMembers")}</p> : (
        <table className="table">
          <tbody>
            {rows.map((m) => (
              <tr key={m.userId}>
                <td><strong>{m.displayName}</strong><div className="muted small">{m.email}</div></td>
                <td>
                  {canEdit ? (
                    <select className="input" aria-label={`${t("projects.role")} ${m.displayName}`} value={m.role}
                      onChange={(e) => void act(() => api.putProjectMember(projectId, m.userId, e.target.value as ProjectRole))}>
                      {PROJECT_ROLES.map((r) => <option key={r} value={r}>{t(`projects.roles.${r}`)}</option>)}
                    </select>
                  ) : t(`projects.roles.${m.role}`)}
                </td>
                <td className="right">
                  {canEdit ? <Button variant="ghost" onClick={() => void act(() => api.removeProjectMember(projectId, m.userId))}>{t("projects.removeMember")}</Button> : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {canEdit && candidates.length > 0 ? (
        <form className="inline-form members-add" onSubmit={(e) => {
          e.preventDefault();
          void act(() => api.putProjectMember(projectId, chosen, role));
        }}>
          <label className="field">
            <span className="field-label">{t("projects.person")}</span>
            <select className="input" value={chosen} onChange={(e) => setPerson(e.target.value)}>
              {candidates.map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
            </select>
          </label>
          <label className="field">
            <span className="field-label">{t("projects.role")}</span>
            <select className="input" value={role} onChange={(e) => setRole(e.target.value as ProjectRole)}>
              {PROJECT_ROLES.map((r) => <option key={r} value={r}>{t(`projects.roles.${r}`)}</option>)}
            </select>
          </label>
          <Button type="submit">{t("projects.addMember")}</Button>
        </form>
      ) : null}
    </Card>
  );
}
