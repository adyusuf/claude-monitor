import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { EstimationUnit } from "../api/typesProjects";
import type { SprintSummary } from "../api/typesSprints";
import { Card, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { SprintBar, useSprintDates, useSprintTitle } from "./SprintBits";

/** The sprints that are running in a project (one per scope at most): nothing is shown while there is none. */
export function ProjectActiveSprints({ ws, projectId, unit }: { ws: string; projectId: string; unit: EstimationUnit }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const title = useSprintTitle();
  const dates = useSprintDates();
  const [rows, setRows] = useState<SprintSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.sprints(projectId, { status: "active", limit: 10 }).then(
      (page) => live && setRows(page.items),
      (e: unknown) => live && setError(errorText(e)),
    );
    return () => {
      live = false;
    };
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  if (rows.length === 0) return error ? <Notice kind="error">{error}</Notice> : null;
  return (
    <Card title={t("sprints.activeSprints")}>
      <ul className="plain">
        {rows.map((s) => (
          <li key={s.id} className="stack">
            <span className="spread">
              <Link to={`/w/${ws}/projects/${projectId}/sprints/${s.id}`}>{title(s)}</Link>
              <span className="muted small">{dates(s)} · {t("sprints.issueCount", { n: s.issueCount })}</span>
            </span>
            <SprintBar sprint={s} unit={unit} />
          </li>
        ))}
      </ul>
    </Card>
  );
}
