import { Link } from "react-router";
import type { IssueSummary } from "../api/typesIssues";
import type { EstimationUnit } from "../api/typesProjects";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { StatusBadge, TypeBadge, useEstimateText, usePriorityText } from "./IssueBits";

interface Props {
  ws: string;
  issue: IssueSummary;
  unit: EstimationUnit;
  canWrite: boolean;
  canMove: boolean;
  first: boolean;
  last: boolean;
  onMove: (direction: -1 | 1) => void;
  onArchive: () => void;
  onRestore: () => void;
}

/** One issue of the backlog: its facts on one line and the actions of the row. The buttons that change things show only to those who may. */
export function BacklogRow({ ws, issue, unit, canWrite, canMove, first, last, onMove, onArchive, onRestore }: Props) {
  const { t } = useI18n();
  const priority = usePriorityText();
  const estimate = useEstimateText();
  const mark = `#${issue.number}`;
  return (
    <li className={issue.archived ? "issue-row dim" : "issue-row"}>
      <div className="issue-main">
        <span className="issue-number">{mark}</span>
        <Link className="issue-title" to={`/w/${ws}/projects/${issue.projectId}/issues/${issue.id}`}>{issue.title}</Link>
        <TypeBadge type={issue.type} />
        {issue.parentId ? <span className="badge" title={t("issues.inEpic")}>↑ {t("projects.itemType.epic")}</span> : null}
        {issue.archived ? <span className="badge warn">{t("projects.archived")}</span> : null}
      </div>
      <div className="issue-meta muted small">
        <span>{t("issues.priorityLabel")}: {priority(issue.priority)}</span>
        <span>{t("issues.estimate")}: {estimate(issue.estimate, unit)}</span>
        <StatusBadge status={issue.status} />
        <span>{issue.assigneeName ?? t("issues.unassigned")}</span>
      </div>
      <div className="issue-actions">
        <Link className="btn btn-ghost" to={`/w/${ws}/projects/${issue.projectId}/issues/${issue.id}`} aria-label={`${t("issues.open")} ${mark}`}>{t("issues.open")}</Link>
        {canWrite && canMove ? (
          <>
            <Button variant="ghost" disabled={first} aria-label={`${t("issues.moveUp")} ${mark}`} onClick={() => onMove(-1)}>↑</Button>
            <Button variant="ghost" disabled={last} aria-label={`${t("issues.moveDown")} ${mark}`} onClick={() => onMove(1)}>↓</Button>
          </>
        ) : null}
        {canWrite ? (
          issue.archived
            ? <Button variant="ghost" aria-label={`${t("projects.restore")} ${mark}`} onClick={onRestore}>{t("projects.restore")}</Button>
            : <Button variant="ghost" aria-label={`${t("projects.archive")} ${mark}`} onClick={onArchive}>{t("projects.archive")}</Button>
        ) : null}
      </div>
    </li>
  );
}
