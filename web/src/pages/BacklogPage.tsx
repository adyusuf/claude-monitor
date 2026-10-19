import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ApplicationSummary, ApplicationView, ProjectView } from "../api/typesProjects";
import { Button, Card, Empty, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { BACKLOG_STATUSES } from "../lib/issueOptions";
import { BacklogFilters } from "./BacklogFilters";
import { BacklogQuickAdd } from "./BacklogQuickAdd";
import { BacklogRow } from "./BacklogRow";
import { noFilters, useBacklog, type BacklogFilters as Filters, type IssueScope } from "./useBacklog";

const SCOPES: IssueScope[] = ["backlog", "all", "archived"];

/** What the backlog shows beside the issues: the project, the people and applications for the filters, and the application on its own backlog. */
function useBacklogContext(ws: string, projectId: string, applicationId: string) {
  const errorText = useErrorText();
  const [project, setProject] = useState<ProjectView | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [applications, setApplications] = useState<ApplicationSummary[]>([]);
  const [application, setApplication] = useState<ApplicationView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [view, people, apps, app] = await Promise.all([
        api.project(projectId), api.members(ws), api.applications(projectId, { status: "all" }), applicationId ? api.application(applicationId) : null,
      ]);
      setProject(view);
      setMembers(people);
      setApplications(apps.items);
      setApplication(app);
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws, projectId, applicationId]);

  useEffect(() => {
    void load();
  }, [load]);

  return { project, members, applications, application, error };
}

/** The issues of a project, or of one application, in the order people gave them; filters, quick add, moving and archiving for those who may. */
export function BacklogPage() {
  const { ws = "", projectId = "", appId = "" } = useParams();
  const { t } = useI18n();
  const { project, members, applications, application, error: loadError } = useBacklogContext(ws, projectId, appId);
  const [scope, setScope] = useState<IssueScope>("backlog");
  const [filters, setFilters] = useState<Filters>({ ...noFilters, application: appId });
  const list = useBacklog(projectId, filters, scope);

  const change = (patch: Partial<Filters>) => setFilters((f) => ({ ...f, ...patch }));
  const chooseScope = (next: IssueScope) => {
    setScope(next);
    if (next === "backlog" && filters.status && !BACKLOG_STATUSES.some((s) => s === filters.status)) change({ status: "" });
  };
  const archive = (id: string, number: number) => {
    const issue = list.rows?.find((x) => x.id === id);
    if (issue && window.confirm(t("issues.archiveConfirm", { number }))) void list.archive(issue);
  };

  if (!project) return loadError ? <div className="page"><Notice kind="error">{loadError}</Notice></div> : <Spinner />;
  const canWrite = project.access.canEditApplications && project.status === "active";
  const back = appId ? `/w/${ws}/projects/${projectId}/applications/${appId}` : `/w/${ws}/projects/${projectId}`;
  const owner = application?.name ?? project.name;
  const error = loadError ?? list.error;
  return (
    <div className="page">
      <Link className="back" to={back}>← {owner}</Link>
      <header className="page-head">
        <h1>{t("issues.backlog")} <span className="muted">· {owner}</span></h1>
        <div className="row-actions" role="group" aria-label={t("issues.scope")}>
          {SCOPES.map((s) => (
            <Button key={s} variant={scope === s ? "primary" : "ghost"} aria-pressed={scope === s} onClick={() => chooseScope(s)}>{t(`issues.scopes.${s}`)}</Button>
          ))}
        </div>
      </header>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {canWrite && scope !== "archived" ? (
        <Card title={t("issues.addIssue")}>
          <BacklogQuickAdd ws={ws} projectId={projectId} itemTypes={project.itemTypes} applicationId={appId || null} />
        </Card>
      ) : null}
      <BacklogFilters filters={filters} scope={scope} onChange={change} itemTypes={project.itemTypes} applications={applications} members={members}
        applicationLocked={Boolean(appId)} />
      {list.rows === null ? <Spinner /> : list.rows.length === 0 ? (
        <Empty title={scope === "archived" ? t("issues.emptyArchived") : t("issues.empty")} hint={canWrite && scope !== "archived" ? t("issues.emptyHint") : undefined} />
      ) : (
        <ul className="plain issue-list">
          {list.rows.map((issue, i) => (
            <BacklogRow key={issue.id} ws={ws} issue={issue} unit={project.estimationUnit} canWrite={canWrite} canMove={scope !== "archived"}
              first={i === 0} last={i === list.rows!.length - 1} onMove={(d) => void list.move(i, d)}
              onArchive={() => archive(issue.id, issue.number)} onRestore={() => void list.restore(issue)} />
          ))}
        </ul>
      )}
      {list.next ? <div className="more"><Button variant="ghost" onClick={() => void list.more()}>{t("projects.more")}</Button></div> : null}
    </div>
  );
}
