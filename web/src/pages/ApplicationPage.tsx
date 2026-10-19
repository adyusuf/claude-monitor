import { useCallback, useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ApplicationInput, ApplicationView } from "../api/typesProjects";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { draftOfApplication } from "../lib/applicationForm";
import { ApplicationComponents } from "./ApplicationComponents";
import { ApplicationDefinition } from "./ApplicationDefinition";
import { ApplicationForm } from "./ApplicationForm";
import { ApplicationReleases } from "./ApplicationReleases";

/** One application: its definition (and a form to change it), its components and modules and its release calendar. */
export function ApplicationPage() {
  const { ws = "", projectId = "", id = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [params, setParams] = useSearchParams();
  const [app, setApp] = useState<ApplicationView | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [error, setError] = useState<string | null>(null);
  const editing = params.get("edit") === "1";

  const load = useCallback(async () => {
    try {
      const [view, people] = await Promise.all([api.application(id), api.members(ws)]);
      setApp(view);
      setMembers(people);
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, ws]);

  useEffect(() => {
    void load();
  }, [load]);

  const setEditing = (on: boolean) => setParams(on ? { edit: "1" } : {}, { replace: true });

  const act = async (work: () => Promise<unknown>) => {
    try {
      setError(null);
      await work();
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const save = async (input: ApplicationInput) => {
    await api.updateApplication(id, input);
    setEditing(false);
    await load();
  };

  const archive = () => {
    if (app && window.confirm(t("projects.archiveConfirm", { name: app.name }))) void act(() => api.archiveApplication(id));
  };

  if (!app) return error ? <div className="page"><Notice kind="error">{error}</Notice></div> : <Spinner />;
  const archived = app.status === "archived";
  const canEdit = app.canEdit && !archived;
  return (
    <div className="page">
      <Link className="back" to={`/w/${ws}/projects/${projectId}`}>← {t("projects.backToProject")}</Link>
      <header className="page-head">
        <h1>{app.name} <span className="badge">{t(`projects.kinds.${app.kind}`)}</span> {archived ? <span className="badge warn">{t("projects.archived")}</span> : null}</h1>
        <div className="row-actions">
          <Link className="btn btn-ghost" to={`/w/${ws}/projects/${projectId}/applications/${id}/backlog`}>{t("issues.backlog")}</Link>
          {canEdit && !editing ? <Button variant="ghost" onClick={() => setEditing(true)}>{t("projects.edit")}</Button> : null}
          {canEdit ? <Button variant="ghost" onClick={archive}>{t("projects.archive")}</Button> : null}
        </div>
      </header>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {archived ? (
        <Notice>
          <span className="spread">
            <span>{t("projects.applicationArchivedBanner")}</span>
            {app.canEdit ? <Button variant="ghost" onClick={() => void act(() => api.restoreApplication(id))}>{t("projects.restore")}</Button> : null}
          </span>
        </Notice>
      ) : null}
      {editing && canEdit ? (
        <Card title={t("projects.editTitle")}>
          <ApplicationForm initial={draftOfApplication(app)} members={members} submitLabel={t("common.save")} onSubmit={save} onCancel={() => setEditing(false)} />
        </Card>
      ) : <ApplicationDefinition app={app} />}
      <ApplicationComponents applicationId={id} canEdit={canEdit} />
      <ApplicationReleases applicationId={id} canEdit={canEdit} />
    </div>
  );
}
