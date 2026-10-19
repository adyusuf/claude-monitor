import { Link } from "react-router";
import type { ApplicationSummary } from "../api/typesProjects";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { Chips } from "./ProjectBits";

/** One application of a project: the name links to it; Edit, Archive and Restore only for those who may change applications. */
export function ApplicationCard({ ws, app, canEdit, onEdit, onArchive, onRestore }: {
  ws: string; app: ApplicationSummary; canEdit: boolean; onEdit: (a: ApplicationSummary) => void; onArchive: (a: ApplicationSummary) => void;
  onRestore: (a: ApplicationSummary) => void;
}) {
  const { t } = useI18n();
  const archived = app.status === "archived";
  return (
    <article className={archived ? "tile dim" : "tile"}>
      <h3 className="tile-title">
        <Link className="stretch" to={`/w/${ws}/projects/${app.projectId}/applications/${app.id}`}>{app.name}</Link>
        <span className="badge">{t(`projects.kinds.${app.kind}`)}</span>
        {archived ? <span className="badge warn">{t("projects.archived")}</span> : null}
      </h3>
      {app.techStack.length > 0 ? <Chips label={t("projects.techStack")} items={app.techStack} /> : null}
      {canEdit ? (
        <div className="tile-actions">
          {archived ? <Button variant="ghost" onClick={() => onRestore(app)}>{t("projects.restore")}</Button> : (
            <>
              <Button variant="ghost" onClick={() => onEdit(app)}>{t("projects.edit")}</Button>
              <Button variant="ghost" onClick={() => onArchive(app)}>{t("projects.archive")}</Button>
            </>
          )}
        </div>
      ) : null}
    </article>
  );
}
