import { useCallback, useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { IssueInput, IssueView } from "../api/typesIssues";
import type { ApplicationSummary, ProjectView } from "../api/typesProjects";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { draftOfIssue } from "../lib/issueForm";
import { IssueCodeLinks } from "./IssueCodeLinks";
import { IssueDetails } from "./IssueDetails";
import { IssueForm } from "./IssueForm";
import { IssueLinks } from "./IssueLinks";
import { IssueRuns } from "./IssueRuns";
import { IssueTestCases } from "./IssueTestCases";
import { StatusBadge } from "./IssueBits";

interface Loaded { view: IssueView; project: ProjectView; members: Member[]; applications: ApplicationSummary[] }

/** One issue: its fields (and a form to change them), what it blocks and is blocked by, its test cases and its pull requests and commits. */
export function IssuePage() {
  const { ws = "", projectId = "", id = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [params, setParams] = useSearchParams();
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [error, setError] = useState<string | null>(null);
  const editing = params.get("edit") === "1";

  const load = useCallback(async () => {
    try {
      const [view, project, members, apps] = await Promise.all([
        api.issue(id), api.project(projectId), api.members(ws), api.applications(projectId, { status: "all" }),
      ]);
      setLoaded({ view, project, members, applications: apps.items });
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, projectId, ws]);

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

  const save = async (input: IssueInput) => {
    await api.updateIssue(id, input);
    setEditing(false);
    await load();
  };

  if (!loaded) return error ? <div className="page"><Notice kind="error">{error}</Notice></div> : <Spinner />;
  const { view, project, members, applications } = loaded;
  const { issue } = view;
  const canWrite = view.canEdit && project.access.canEditApplications;
  const writable = canWrite && !issue.archived;
  const archive = () => {
    if (window.confirm(t("issues.archiveConfirm", { number: issue.number }))) void act(() => api.archiveIssue(id));
  };
  return (
    <div className="page">
      <Link className="back" to={`/w/${ws}/projects/${projectId}/backlog`}>← {t("issues.backlog")}</Link>
      <header className="page-head">
        <h1>#{issue.number} {issue.title} <StatusBadge status={issue.status} /> {issue.archived ? <span className="badge warn">{t("projects.archived")}</span> : null}</h1>
        <div className="row-actions">
          {writable && !editing ? <Button variant="ghost" onClick={() => setEditing(true)}>{t("projects.edit")}</Button> : null}
          {writable ? <Button variant="ghost" onClick={archive}>{t("projects.archive")}</Button> : null}
        </div>
      </header>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {issue.archived ? (
        <Notice>
          <span className="spread">
            <span>{t("issues.archivedBanner")}</span>
            {canWrite ? <Button variant="ghost" onClick={() => void act(() => api.restoreIssue(id))}>{t("projects.restore")}</Button> : null}
          </span>
        </Notice>
      ) : null}
      {editing && writable ? (
        <Card title={t("projects.editTitle")}>
          <IssueForm initial={draftOfIssue(view)} issueId={id} project={project} members={members} applications={applications} onSubmit={save} onCancel={() => setEditing(false)} />
        </Card>
      ) : <IssueDetails ws={ws} view={view} project={project} applications={applications} />}
      <IssueLinks ws={ws} projectId={projectId} issueId={id} blocks={view.blocks} blockedBy={view.blockedBy} canWrite={writable} onChanged={load} />
      <IssueTestCases issueId={id} canWrite={writable} />
      <IssueCodeLinks issueId={id} canWrite={writable} />
      <IssueRuns ws={ws} issueId={id} canWrite={writable} />
    </div>
  );
}
