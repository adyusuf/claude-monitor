import { useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import { Button, Card, Empty, Notice, Spinner } from "../components/ui";
import { useI18n } from "../i18n";
import { draftOfSprint, inputOfSprint, teamCandidates, type SprintDraft } from "../lib/sprintForm";
import { unfinishedCount } from "../lib/sprintOptions";
import { SprintAddIssues } from "./SprintAddIssues";
import { SprintBoard } from "./SprintBoard";
import { SprintClosePanel } from "./SprintClosePanel";
import { SprintFacts } from "./SprintFacts";
import { SprintForm } from "./SprintForm";
import { useSprintTitle } from "./SprintBits";
import { useSprintContext } from "./useSprintContext";
import { useSprintReleases } from "./useSprintReleases";
import { useSprintView } from "./useSprintView";

/**
 * One sprint: its facts (and a form to change them), the actions of its life (start, close, delete), its issues by status and a panel to add issues.
 * What changes follows the API's answer (the sprint's canEdit and the project's rights); a closed sprint is read-only.
 */
export function SprintPage() {
  const { ws = "", projectId = "", id = "" } = useParams();
  const { t } = useI18n();
  const navigate = useNavigate();
  const title = useSprintTitle();
  const [params, setParams] = useSearchParams();
  const [closing, setClosing] = useState(false);
  const sprint = useSprintView(id, projectId);
  const { view } = sprint;
  const applicationId = view?.sprint.applicationId ?? "";
  const context = useSprintContext(ws, projectId, applicationId);
  const editing = params.get("edit") === "1";
  const releases = useSprintReleases(context.applications, applicationId, editing && view !== null);
  const { project } = context;

  const setEditing = (on: boolean) => setParams(on ? { edit: "1" } : {}, { replace: true });
  const changed = async () => {
    await sprint.act(async () => undefined);
  };
  const save = async (draft: SprintDraft) => {
    await api.updateSprint(id, inputOfSprint(draft));
    setEditing(false);
    await changed();
  };
  const close = async (moveTo: string | null) => {
    if (await sprint.act(() => api.closeSprint(id, moveTo))) setClosing(false);
  };
  const remove = async () => {
    if (view && window.confirm(t("sprints.deleteConfirm", { n: view.sprint.number }))
      && await sprint.act(() => api.deleteSprint(id), false)) navigate(`/w/${ws}/projects/${projectId}/sprints`);
  };

  const error = sprint.error ?? context.error ?? releases.error;
  if (!view || !project) return error ? <div className="page"><Notice kind="error">{error}</Notice></div> : <Spinner />;
  const s = view.sprint;
  const closed = s.status === "closed";
  const canEdit = view.canEdit && project.access.canEditApplications && project.status === "active" && !closed;
  const unit = project.estimationUnit;
  const kept = releases.options.some((r) => r.id === s.releaseId) || !s.releaseId ? releases.options : [...releases.options, { id: s.releaseId, label: s.releaseName ?? s.releaseId }];
  return (
    <div className="page">
      <Link className="back" to={`/w/${ws}/projects/${projectId}/sprints`}>← {t("sprints.backToSprints")}</Link>
      <header className="page-head">
        <h1>{title(s)}</h1>
        {canEdit ? (
          <div className="row-actions">
            {!editing ? <Button variant="ghost" onClick={() => setEditing(true)}>{t("sprints.edit")}</Button> : null}
            {s.status === "planned" ? <Button onClick={() => void sprint.act(() => api.startSprint(id))}>{t("sprints.start")}</Button> : null}
            {s.status === "active" && !closing ? <Button onClick={() => setClosing(true)}>{t("sprints.close")}</Button> : null}
            {s.status === "planned" ? <Button variant="ghost" onClick={() => void remove()}>{t("sprints.delete")}</Button> : null}
          </div>
        ) : null}
      </header>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {closed ? <Notice>{t("sprints.closedBanner")}</Notice> : null}
      {editing && canEdit ? (
        <Card title={t("sprints.editTitle")}>
          <SprintForm initial={draftOfSprint(view)} project={project} people={teamCandidates(context.members, view.team.map((m) => m.userId))}
            releases={kept} submitLabel={t("common.save")} onSubmit={save} onCancel={() => setEditing(false)} />
        </Card>
      ) : <SprintFacts view={view} unit={unit} />}
      {closing && canEdit ? (
        <SprintClosePanel sprint={s} unfinished={unfinishedCount(view.statusCounts)} onConfirm={close} onCancel={() => setClosing(false)} />
      ) : null}
      <Card title={t("sprints.boardTitle")}>
        {sprint.issues === null ? <Spinner /> : sprint.issues.length === 0 ? <Empty title={t("sprints.noIssues")} /> : (
          <SprintBoard ws={ws} issues={sprint.issues} unit={unit} canEdit={canEdit}
            onStatus={(issue, status) => void sprint.act(() => api.setIssueStatus(issue.id, status))}
            onRemove={(issue) => void sprint.act(() => api.removeSprintIssue(id, issue.id))} />
        )}
        {sprint.next ? <div className="more"><Button variant="ghost" onClick={() => void sprint.more()}>{t("projects.more")}</Button></div> : null}
      </Card>
      {canEdit ? <SprintAddIssues sprint={s} unit={unit} itemTypes={project.itemTypes} version={sprint.version} onChanged={changed} /> : null}
    </div>
  );
}
