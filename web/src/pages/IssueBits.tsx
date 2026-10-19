import type { EstimationUnit, ItemType } from "../api/typesProjects";
import type { IssueStatus } from "../api/typesIssues";
import { useI18n } from "../i18n";

export function TypeBadge({ type }: { type: ItemType }) {
  const { t } = useI18n();
  return <span className="badge">{t(`projects.itemType.${type}`)}</span>;
}

/** A finished issue is green, a cancelled one amber, the rest neutral. */
export function StatusBadge({ status }: { status: IssueStatus }) {
  const { t } = useI18n();
  const tone = status === "done" ? " ok" : status === "cancelled" ? " warn" : "";
  return <span className={`badge${tone}`}>{t(`issues.status.${status}`)}</span>;
}

/** The text of a priority value (a value of the project's scale), or a dash. */
export function usePriorityText() {
  const { tx } = useI18n();
  return (value: string | null | undefined): string => value ? tx(`issues.priority.${value}`) : "–";
}

/** An estimate with its unit: "5 pts", "3 h", "2 d"; a T-shirt size in capitals; a dash when there is none. */
export function useEstimateText() {
  const { t } = useI18n();
  return (value: string | null | undefined, unit: EstimationUnit): string => {
    if (!value) return "–";
    return unit === "t_shirt" ? value.toUpperCase() : `${value} ${t(`issues.unitShort.${unit}`)}`;
  };
}
