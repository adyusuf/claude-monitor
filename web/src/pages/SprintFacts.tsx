import type { EstimationUnit } from "../api/typesProjects";
import type { SprintView } from "../api/typesSprints";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";
import { unitIsNumeric } from "../lib/sprintOptions";
import { Chips } from "./ProjectBits";
import { SprintBar, SprintStatusBadge, useSprintDates, useSprintTitle, useUnitAmount } from "./SprintBits";

/** What a sprint is: its number, state, dates, goal, capacity against what is committed and done, release and team. */
export function SprintFacts({ view, unit }: { view: SprintView; unit: EstimationUnit }) {
  const { t } = useI18n();
  const title = useSprintTitle();
  const dates = useSprintDates();
  const amount = useUnitAmount(unit);
  const { sprint: s } = view;
  const none = <span className="muted">–</span>;
  return (
    <Card title={<>{title(s)} <SprintStatusBadge status={s.status} /></>}>
      <dl className="facts">
        <dt>{t("sprints.goalLabel")}</dt><dd className="pre">{s.goal ?? none}</dd>
        <dt>{t("sprints.datesLabel")}</dt><dd>{dates(s)}</dd>
        <dt>{t("sprints.capacity")}</dt><dd>{s.capacity === null ? none : amount(s.capacity)}</dd>
        {unitIsNumeric(unit) ? (
          <>
            <dt>{t("sprints.committed")}</dt><dd>{amount(s.committed)}</dd>
            <dt>{t("sprints.done")}</dt><dd>{amount(s.done)}</dd>
          </>
        ) : null}
        <dt>{t("sprints.issuesLabel")}</dt><dd>{s.issueCount}</dd>
        <dt>{t("sprints.release")}</dt><dd>{s.releaseName ?? none}</dd>
        <dt>{t("sprints.team")}</dt>
        <dd>{view.team.length === 0 ? <span className="muted">{t("sprints.noTeam")}</span> : <Chips label={t("sprints.teamOf")} items={view.team.map((m) => m.displayName)} />}</dd>
      </dl>
      <SprintBar sprint={s} unit={unit} />
    </Card>
  );
}
