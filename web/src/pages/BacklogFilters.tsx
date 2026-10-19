import type { ReactNode } from "react";
import type { Member } from "../api/types";
import type { ApplicationSummary, ItemType } from "../api/typesProjects";
import { Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { ISSUE_STATUSES, BACKLOG_STATUSES } from "../lib/issueOptions";
import { useApplicationParts } from "./useApplicationParts";
import type { BacklogFilters as Filters, IssueScope } from "./useBacklog";

function Pick({ label, value, onChange, disabled, children }: {
  label: string; value: string; onChange: (value: string) => void; disabled?: boolean; children: ReactNode;
}) {
  return (
    <select className="input filter" aria-label={label} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
      <option value="">{label}</option>
      {children}
    </select>
  );
}

/** The search box and the selects that narrow the list. Component and release depend on the application, which is fixed on an application's own backlog. */
export function BacklogFilters({ filters, scope, onChange, itemTypes, applications, members, applicationLocked }: {
  filters: Filters; scope: IssueScope; onChange: (patch: Partial<Filters>) => void; itemTypes: ItemType[];
  applications: ApplicationSummary[]; members: Member[]; applicationLocked: boolean;
}) {
  const { t } = useI18n();
  const parts = useApplicationParts(filters.application);
  const statuses = scope === "backlog" ? BACKLOG_STATUSES : ISSUE_STATUSES;
  return (
    <div className="toolbar filters">
      <input className="input search" type="search" placeholder={t("issues.search")} aria-label={t("issues.search")} value={filters.q}
        onChange={(e) => onChange({ q: e.target.value })} />
      <Pick label={t("issues.type")} value={filters.type} onChange={(type) => onChange({ type })}>
        {itemTypes.map((x) => <option key={x} value={x}>{t(`projects.itemType.${x}`)}</option>)}
      </Pick>
      <Pick label={t("issues.statusLabel")} value={filters.status} onChange={(status) => onChange({ status })}>
        {statuses.map((x) => <option key={x} value={x}>{t(`issues.status.${x}`)}</option>)}
      </Pick>
      <Pick label={t("issues.assignee")} value={filters.assignee} onChange={(assignee) => onChange({ assignee })}>
        {members.map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
      </Pick>
      <Pick label={t("issues.application")} value={filters.application} disabled={applicationLocked}
        onChange={(application) => onChange({ application, component: "", release: "" })}>
        {applications.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
      </Pick>
      <Pick label={t("issues.component")} value={filters.component} disabled={!filters.application} onChange={(component) => onChange({ component })}>
        {parts.components.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
      </Pick>
      <Pick label={t("issues.release")} value={filters.release} disabled={!filters.application} onChange={(release) => onChange({ release })}>
        {parts.releases.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
      </Pick>
      {parts.error ? <Notice kind="error">{parts.error}</Notice> : null}
    </div>
  );
}
