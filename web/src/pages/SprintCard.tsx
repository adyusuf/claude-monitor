import { Link } from "react-router";
import type { EstimationUnit } from "../api/typesProjects";
import type { SprintSummary } from "../api/typesSprints";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { SprintBar, SprintStatusBadge, useSprintDates, useSprintTitle } from "./SprintBits";

/** One sprint in the list. The title links to the sprint; the buttons (only for those who may, and only on a planned sprint) sit above that link. */
export function SprintCard({ ws, sprint, unit, scopeName, canWrite, onStart, onDelete }: {
  ws: string; sprint: SprintSummary; unit: EstimationUnit; scopeName: string | null; canWrite: boolean;
  onStart: (s: SprintSummary) => void; onDelete: (s: SprintSummary) => void;
}) {
  const { t } = useI18n();
  const title = useSprintTitle();
  const dates = useSprintDates();
  return (
    <article className={sprint.status === "closed" ? "tile dim" : "tile"}>
      <h2 className="tile-title">
        <Link className="stretch" to={`/w/${ws}/projects/${sprint.projectId}/sprints/${sprint.id}`}>{title(sprint)}</Link>
        <SprintStatusBadge status={sprint.status} />
      </h2>
      {sprint.goal ? <p className="tile-text">{sprint.goal}</p> : null}
      <p className="muted small tile-meta">
        <span>{dates(sprint)}</span>
        <span>{t("sprints.issueCount", { n: sprint.issueCount })}</span>
        {sprint.releaseName ? <span>{t("sprints.releaseIs", { name: sprint.releaseName })}</span> : null}
        {scopeName ? <span>{t("sprints.forApplication", { name: scopeName })}</span> : null}
      </p>
      <SprintBar sprint={sprint} unit={unit} />
      {canWrite && sprint.status === "planned" ? (
        <div className="tile-actions">
          <Button variant="ghost" aria-label={`${t("sprints.start")} ${title(sprint)}`} onClick={() => onStart(sprint)}>{t("sprints.start")}</Button>
          <Button variant="ghost" aria-label={`${t("sprints.delete")} ${title(sprint)}`} onClick={() => onDelete(sprint)}>{t("sprints.delete")}</Button>
        </div>
      ) : null}
    </article>
  );
}
