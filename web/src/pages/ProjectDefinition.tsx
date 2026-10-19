import type { ProjectView } from "../api/typesProjects";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";
import { day } from "../lib/format";
import { Chips } from "./ProjectBits";

function Lines({ lines }: { lines: string[] }) {
  return lines.length === 0 ? <span className="muted">–</span> : <ul className="bullets">{lines.map((line, i) => <li key={i}>{line}</li>)}</ul>;
}

/** What a project is: every field of its definition, as text. */
export function ProjectDefinition({ project: p }: { project: ProjectView }) {
  const { t } = useI18n();
  const none = <span className="muted">–</span>;
  return (
    <Card title={t("projects.definition")}>
      <dl className="facts">
        <dt>{t("projects.purpose")}</dt><dd className="pre">{p.purpose ?? none}</dd>
        <dt>{t("projects.owner")}</dt><dd>{p.ownerName ?? none}</dd>
        <dt>{t("projects.sponsor")}</dt><dd>{p.sponsor ?? none}</dd>
        <dt>{t("projects.stakeholders")}</dt>
        <dd>
          {p.stakeholders.length === 0 ? none : (
            <ul className="bullets">{p.stakeholders.map((s, i) => <li key={i}>{s.name}{s.role ? <span className="muted"> · {s.role}</span> : null}</li>)}</ul>
          )}
        </dd>
        <dt>{t("projects.budget")}</dt><dd>{p.budgetAmount === null ? none : `${p.budgetAmount} ${p.budgetCurrency ?? ""}`}</dd>
        <dt>{t("projects.dates")}</dt><dd>{p.startDate || p.endDate ? `${day(p.startDate) || "…"} – ${day(p.endDate) || "…"}` : none}</dd>
        <dt>{t("projects.itemTypes")}</dt><dd><Chips label={t("projects.itemTypes")} items={p.itemTypes.map((x) => t(`projects.itemType.${x}`))} /></dd>
        <dt>{t("projects.priorityScale")}</dt><dd>{t(`projects.priority.${p.priorityScale}`)}</dd>
        <dt>{t("projects.estimationUnit")}</dt><dd>{t(`projects.estimation.${p.estimationUnit}`)}</dd>
        <dt>{t("projects.sprintLength")}</dt><dd>{t("projects.sprintDays", { n: p.sprintLengthDays })}</dd>
        <dt>{t("projects.definitionOfReady")}</dt><dd><Lines lines={p.definitionOfReady} /></dd>
        <dt>{t("projects.definitionOfDone")}</dt><dd><Lines lines={p.definitionOfDone} /></dd>
      </dl>
    </Card>
  );
}
