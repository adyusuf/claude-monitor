import { Link } from "react-router";
import type { IssueStatus, IssueSummary } from "../api/typesIssues";
import type { EstimationUnit } from "../api/typesProjects";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { ISSUE_STATUSES } from "../lib/issueOptions";
import { TypeBadge, useEstimateText } from "./IssueBits";

interface CardProps {
  ws: string;
  issue: IssueSummary;
  unit: EstimationUnit;
  canEdit: boolean;
  onStatus: (status: IssueStatus) => void;
  onRemove: () => void;
}

function BoardCard({ ws, issue, unit, canEdit, onStatus, onRemove }: CardProps) {
  const { t } = useI18n();
  const estimate = useEstimateText();
  const mark = `#${issue.number}`;
  return (
    <li className="sprint-issue">
      <div className="issue-main">
        <span className="issue-number">{mark}</span>
        <Link className="issue-title" to={`/w/${ws}/projects/${issue.projectId}/issues/${issue.id}`}>{issue.title}</Link>
      </div>
      <div className="issue-meta muted small">
        <TypeBadge type={issue.type} />
        <span>{t("issues.estimate")}: {estimate(issue.estimate, unit)}</span>
        <span>{issue.assigneeName ?? t("issues.unassigned")}</span>
      </div>
      {canEdit ? (
        <div className="sprint-issue-actions">
          <select className="input" aria-label={t("sprints.statusOf", { mark })} value={issue.status} onChange={(e) => onStatus(e.target.value as IssueStatus)}>
            {ISSUE_STATUSES.map((s) => <option key={s} value={s}>{t(`issues.status.${s}`)}</option>)}
          </select>
          <Button variant="ghost" aria-label={t("sprints.removeFrom", { mark })} onClick={onRemove}>{t("sprints.remove")}</Button>
        </div>
      ) : null}
    </li>
  );
}

/** The issues of a sprint in one column per status. Those who may change the sprint get a status select and a remove button on each issue. */
export function SprintBoard({ ws, issues, unit, canEdit, onStatus, onRemove }: {
  ws: string; issues: IssueSummary[]; unit: EstimationUnit; canEdit: boolean;
  onStatus: (issue: IssueSummary, status: IssueStatus) => void; onRemove: (issue: IssueSummary) => void;
}) {
  const { t } = useI18n();
  return (
    <div className="sprint-board">
      {ISSUE_STATUSES.map((status) => {
        const inColumn = issues.filter((i) => i.status === status);
        return (
          <section key={status} className="sprint-column" aria-label={t(`issues.status.${status}`)}>
            <h3>{t(`issues.status.${status}`)} <span className="muted">{inColumn.length}</span></h3>
            {inColumn.length === 0 ? <p className="muted small">{t("sprints.noneInColumn")}</p> : (
              <ul className="plain">
                {inColumn.map((issue) => (
                  <BoardCard key={issue.id} ws={ws} issue={issue} unit={unit} canEdit={canEdit}
                    onStatus={(s) => onStatus(issue, s)} onRemove={() => onRemove(issue)} />
                ))}
              </ul>
            )}
          </section>
        );
      })}
    </div>
  );
}
