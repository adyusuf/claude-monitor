import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ApplicationInput, ApplicationSummary } from "../api/typesProjects";
import { Button, Card, Empty, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { draftOfApplication, emptyApplicationDraft, type ApplicationDraft } from "../lib/applicationForm";
import { ApplicationCard } from "./ApplicationCard";
import { ApplicationForm } from "./ApplicationForm";

type Editing = { id: string | null; draft: ApplicationDraft };

/** The applications of a project as cards (archived ones too, marked), with a form to add or change one for those who may. */
export function ProjectApplications({ ws, projectId, canEdit, workspaceMembers }: {
  ws: string; projectId: string; canEdit: boolean; workspaceMembers: Member[];
}) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<ApplicationSummary[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const page = await api.applications(projectId, { status: "all", limit: config.pageSize });
      setRows(page.items);
      setNext(page.next);
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

  const edit = (a: ApplicationSummary) => act(async () => setEditing({ id: a.id, draft: draftOfApplication(await api.application(a.id)) }));
  const archive = (a: ApplicationSummary) => {
    if (window.confirm(t("projects.archiveConfirm", { name: a.name }))) void act(() => api.archiveApplication(a.id));
  };
  const more = async () => {
    try {
      const page = await api.applications(projectId, { status: "all", cursor: next, limit: config.pageSize });
      setRows((r) => [...(r ?? []), ...page.items]);
      setNext(page.next);
    } catch (e) {
      setError(errorText(e));
    }
  };

  const save = async (input: ApplicationInput) => {
    if (editing?.id) await api.updateApplication(editing.id, input);
    else await api.createApplication(projectId, input);
    setEditing(null);
    await load();
  };

  return (
    <Card title={t("projects.applications")}
      actions={canEdit && !editing ? <Button onClick={() => setEditing({ id: null, draft: emptyApplicationDraft() })}>{t("projects.addApplication")}</Button> : undefined}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {editing ? (
        <ApplicationForm key={editing.id ?? "new"} initial={editing.draft} members={workspaceMembers} submitLabel={editing.id ? t("common.save") : t("projects.create")}
          onSubmit={save} onCancel={() => setEditing(null)} />
      ) : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <Empty title={t("projects.noApplications")} hint={canEdit ? t("projects.noApplicationsHint") : undefined} /> : (
        <div className="tiles">
          {rows.map((a) => (
            <ApplicationCard key={a.id} ws={ws} app={a} canEdit={canEdit} onEdit={(x) => void edit(x)} onArchive={archive}
              onRestore={(x) => void act(() => api.restoreApplication(x.id))} />
          ))}
        </div>
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void more()}>{t("projects.more")}</Button></div> : null}
    </Card>
  );
}
