import { Link } from "react-router";
import type { ProjectSummary } from "../api/typesProjects";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { day } from "../lib/format";

/** One project in the list. The name links to the project; the buttons (only for roles that may use them) sit above that link. */
export function ProjectCard({ ws, project: p, canWork, onArchive, onRestore }: {
  ws: string; project: ProjectSummary; canWork: boolean; onArchive: (p: ProjectSummary) => void; onRestore: (p: ProjectSummary) => void;
}) {
  const { t } = useI18n();
  const archived = p.status === "archived";
  return (
    <article className={archived ? "tile dim" : "tile"}>
      <h2 className="tile-title">
        <Link className="stretch" to={`/w/${ws}/projects/${p.id}`}>{p.name}</Link>
        {archived ? <span className="badge warn">{t("projects.archived")}</span> : null}
        {p.sourceConnectionId ? <span className="badge">{t("integrations.fromJira")}</span> : null}
      </h2>
      {p.purpose ? <p className="tile-text">{p.purpose}</p> : null}
      <p className="muted small tile-meta">
        <span>{t("projects.ownerIs", { name: p.ownerName ?? "–" })}</span>
        <span>{t("projects.applicationCount", { n: p.applicationCount })}</span>
        {p.startDate || p.endDate ? <span>{day(p.startDate) || "…"} – {day(p.endDate) || "…"}</span> : null}
      </p>
      {canWork ? (
        <div className="tile-actions">
          {archived ? <Button variant="ghost" onClick={() => onRestore(p)}>{t("projects.restore")}</Button> : (
            <>
              <Link className="btn btn-ghost" to={`/w/${ws}/projects/${p.id}?edit=1`}>{t("projects.edit")}</Link>
              <Button variant="ghost" onClick={() => onArchive(p)}>{t("projects.archive")}</Button>
            </>
          )}
        </div>
      ) : null}
    </article>
  );
}
