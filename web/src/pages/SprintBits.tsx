import type { EstimationUnit } from "../api/typesProjects";
import type { SprintStatus, SprintSummary } from "../api/typesSprints";
import { useI18n } from "../i18n";
import { day } from "../lib/format";

/** "Sprint 3" or "Sprint 3 · Payments" when the sprint has a name. */
export function useSprintTitle() {
  const { t } = useI18n();
  return (s: Pick<SprintSummary, "number" | "name">): string => s.name ? t("sprints.sprintNamed", { n: s.number, name: s.name }) : t("sprints.sprint", { n: s.number });
}

/** A running sprint is green, a closed one neutral, a planned one amber. */
export function SprintStatusBadge({ status }: { status: SprintStatus }) {
  const { t } = useI18n();
  const tone = status === "active" ? " ok" : status === "planned" ? " warn" : "";
  return <span className={`badge${tone}`}>{t(`sprints.status.${status}`)}</span>;
}

/** "05/10/2026 – 19/10/2026", or a note while the sprint has no dates. */
export function useSprintDates() {
  const { t } = useI18n();
  return (s: Pick<SprintSummary, "startDate" | "endDate">): string =>
    s.startDate || s.endDate ? t("sprints.dates", { start: day(s.startDate) || "…", end: day(s.endDate) || "…" }) : t("sprints.noDates");
}

/** A number of estimate units: "12 h". */
export function useUnitAmount(unit: EstimationUnit) {
  const { t } = useI18n();
  return (n: number): string => unit === "t_shirt" ? String(n) : t("sprints.estimateIn", { n, unit: t(`issues.unitShort.${unit}`) });
}

/** The committed estimates (and the finished part of them) against the capacity. Only a numeric unit with a capacity has a bar; a T-shirt project has none. */
export function SprintBar({ sprint, unit }: { sprint: SprintSummary; unit: EstimationUnit }) {
  const { t } = useI18n();
  const capacity = sprint.capacity ?? 0;
  if (unit === "t_shirt" || capacity <= 0) return null;
  const width = (n: number) => `${Math.min(100, Math.round((n / capacity) * 100))}%`;
  return (
    <div className="sprint-meter">
      <div className="sprint-bar" role="progressbar" aria-label={t("sprints.barLabel")} aria-valuemin={0} aria-valuemax={capacity} aria-valuenow={sprint.committed}>
        <span className="sprint-bar-committed" style={{ width: width(sprint.committed) }} />
        <span className="sprint-bar-done" style={{ width: width(sprint.done) }} />
      </div>
      <span className="muted small">{t("sprints.committedOf", { committed: sprint.committed, capacity, unit: t(`issues.unitShort.${unit}`) })}</span>
    </div>
  );
}
