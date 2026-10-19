import { Link } from "react-router";
import type { IssueView } from "../api/typesIssues";
import type { ApplicationSummary, ProjectView } from "../api/typesProjects";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";
import { date } from "../lib/format";
import { StatusBadge, TypeBadge, useEstimateText, usePriorityText } from "./IssueBits";
import { useApplicationParts } from "./useApplicationParts";

/** What an issue is: every field as text, the bug fields only for a bug. */
export function IssueDetails({ ws, view, project, applications }: { ws: string; view: IssueView; project: ProjectView; applications: ApplicationSummary[] }) {
  const { t } = useI18n();
  const priority = usePriorityText();
  const estimate = useEstimateText();
  const { issue } = view;
  const parts = useApplicationParts(issue.applicationId ?? "");
  const none = <span className="muted">–</span>;
  const app = applications.find((a) => a.id === issue.applicationId);
  const component = parts.components.find((c) => c.id === issue.componentId);
  const release = parts.releases.find((r) => r.id === issue.releaseId);
  const base = `/w/${ws}/projects/${issue.projectId}`;
  return (
    <Card title={t("projects.definition")}>
      <dl className="facts">
        <dt>{t("issues.type")}</dt><dd><TypeBadge type={issue.type} /></dd>
        <dt>{t("issues.statusLabel")}</dt><dd><StatusBadge status={issue.status} /></dd>
        <dt>{t("issues.priorityLabel")}</dt><dd>{priority(issue.priority)}</dd>
        <dt>{t("issues.estimate")}</dt><dd>{estimate(issue.estimate, project.estimationUnit)}</dd>
        <dt>{t("issues.assignee")}</dt><dd>{issue.assigneeName ?? none}</dd>
        <dt>{t("issues.parent")}</dt>
        <dd>{issue.parentId ? <Link to={`${base}/issues/${issue.parentId}`}>{view.parentTitle ?? t("issues.open")}</Link> : none}</dd>
        <dt>{t("issues.application")}</dt>
        <dd>{app ? <Link to={`${base}/applications/${app.id}`}>{app.name}</Link> : none}</dd>
        <dt>{t("issues.component")}</dt><dd>{component?.name ?? none}</dd>
        <dt>{t("issues.release")}</dt><dd>{release?.name ?? none}</dd>
        <dt>{t("issues.created")}</dt><dd>{date(issue.createdAt)}</dd>
        <dt>{t("issues.description")}</dt><dd className="pre">{view.description ?? none}</dd>
        <dt>{t("issues.criteria")}</dt>
        <dd>
          {view.acceptanceCriteria.length === 0 ? none : (
            <ul className="bullets">
              {view.acceptanceCriteria.map((c, i) => (
                <li key={i}>
                  {c.given ? <div><strong>{t("issues.given")}</strong> {c.given}</div> : null}
                  {c.when ? <div><strong>{t("issues.when")}</strong> {c.when}</div> : null}
                  {c.then ? <div><strong>{t("issues.then")}</strong> {c.then}</div> : null}
                </li>
              ))}
            </ul>
          )}
        </dd>
        <dt>{t("issues.technicalDesign")}</dt><dd className="pre">{view.technicalDesign ?? none}</dd>
        {issue.type === "bug" ? (
          <>
            <dt>{t("issues.bugEnvironment")}</dt><dd className="pre">{view.bugEnvironment ?? none}</dd>
            <dt>{t("issues.reproSteps")}</dt><dd className="pre">{view.reproSteps ?? none}</dd>
            <dt>{t("issues.expectedResult")}</dt><dd className="pre">{view.expectedResult ?? none}</dd>
            <dt>{t("issues.actualResult")}</dt><dd className="pre">{view.actualResult ?? none}</dd>
            <dt>{t("issues.severity")}</dt><dd>{view.severity ? t(`issues.severities.${view.severity}`) : none}</dd>
          </>
        ) : null}
      </dl>
    </Card>
  );
}
