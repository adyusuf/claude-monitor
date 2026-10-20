import { useCallback, useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ProjectInput, ProjectView } from "../api/typesProjects";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { draftOfProject } from "../lib/projectForm";
import { ProjectActiveSprints } from "./ProjectActiveSprints";
import { ProjectApplications } from "./ProjectApplications";
import { ProjectDefinition } from "./ProjectDefinition";
import { ProjectForm } from "./ProjectForm";
import { ProjectMembers } from "./ProjectMembers";

/** One managed project: its definition (and a form to change it), its people and its applications. The buttons follow what the API says this person may do. */
export function ProjectPage() {
  const { ws = "", id = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [params, setParams] = useSearchParams();
  const [project, setProject] = useState<ProjectView | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [error, setError] = useState<string | null>(null);
  const editing = params.get("edit") === "1";

  const load = useCallback(async () => {
    try {
      const [view, people] = await Promise.all([api.project(id), api.members(ws)]);
      setProject(view);
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

  const save = async (input: ProjectInput) => {
    await api.updateProject(id, input);
    setEditing(false);
    await load();
  };

  const archive = () => {
    if (project && window.confirm(t("projects.archiveConfirm", { name: project.name }))) void act(() => api.archiveProject(id));
  };

  if (!project) return error ? <div className="page"><Notice kind="error">{error}</Notice></div> : <Spinner />;
  const archived = project.status === "archived";
  const { access } = project;
  return (
    <div className="page">
      <Link className="back" to={`/w/${ws}/projects`}>← {t("projects.title")}</Link>
      <header className="page-head">
        <h1>{project.name} {archived ? <span className="badge warn">{t("projects.archived")}</span> : <span className="badge ok">{t("projects.active")}</span>}</h1>
        <div className="row-actions">
          <Link className="btn btn-ghost" to={`/w/${ws}/projects/${id}/backlog`}>{t("issues.backlog")}</Link>
          <Link className="btn btn-ghost" to={`/w/${ws}/projects/${id}/sprints`}>{t("sprints.title")}</Link>
          {access.canEditApplications && !archived ? <Link className="btn btn-ghost" to={`/w/${ws}/projects/${id}/sprints?new=1`}>{t("sprints.startSprint")}</Link> : null}
          {access.canEdit && !archived && !editing ? <Button variant="ghost" onClick={() => setEditing(true)}>{t("projects.edit")}</Button> : null}
          {access.canArchive && !archived ? <Button variant="ghost" onClick={archive}>{t("projects.archive")}</Button> : null}
        </div>
      </header>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {archived ? (
        <Notice>
          <span className="spread">
            <span>{t("projects.archivedBanner")}</span>
            {access.canArchive ? <Button variant="ghost" onClick={() => void act(() => api.restoreProject(id))}>{t("projects.restore")}</Button> : null}
          </span>
        </Notice>
      ) : null}
      {editing && access.canEdit && !archived ? (
        <Card title={t("projects.editTitle")}>
          <ProjectForm initial={draftOfProject(project)} members={members} canChangeOwner={access.canArchive} submitLabel={t("common.save")} mirrored={project.sourceConnectionId !== null}
            onSubmit={save} onCancel={() => setEditing(false)} />
        </Card>
      ) : <ProjectDefinition project={project} />}
      <ProjectActiveSprints ws={ws} projectId={id} unit={project.estimationUnit} />
      <ProjectMembers projectId={id} workspaceMembers={members} canEdit={access.canEdit && !archived} />
      <ProjectApplications ws={ws} projectId={id} canEdit={access.canEditApplications && !archived} workspaceMembers={members} />
    </div>
  );
}
