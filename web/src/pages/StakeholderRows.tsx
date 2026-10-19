import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { projectLimits } from "../lib/projectOptions";
import type { ProjectDraft } from "../lib/projectForm";
import { Group } from "./ProjectBits";

type Row = ProjectDraft["stakeholders"][number];

/** The stakeholders of a project: a name and an optional role each. */
export function StakeholderRows({ rows, onChange, error }: { rows: Row[]; onChange: (rows: Row[]) => void; error?: string }) {
  const { t } = useI18n();
  const set = (i: number, patch: Partial<Row>) => onChange(rows.map((r, j) => j === i ? { ...r, ...patch } : r));
  return (
    <Group legend={t("projects.stakeholders")} error={error}>
      {rows.map((row, i) => (
        <div key={i} className="row-input">
          <input className="input" aria-label={`${t("projects.stakeholderName")} ${i + 1}`} maxLength={projectLimits.nameMax} value={row.name}
            onChange={(e) => set(i, { name: e.target.value })} />
          <input className="input" aria-label={`${t("projects.stakeholderRole")} ${i + 1}`} maxLength={projectLimits.nameMax} value={row.role}
            onChange={(e) => set(i, { role: e.target.value })} />
          <Button variant="subtle" aria-label={`${t("projects.removeRow")} ${t("projects.stakeholders")} ${i + 1}`} onClick={() => onChange(rows.filter((_, j) => j !== i))}>×</Button>
        </div>
      ))}
      {rows.length < projectLimits.stakeholdersMax ? <div><Button variant="ghost" onClick={() => onChange([...rows, { name: "", role: "" }])}>{t("projects.addStakeholder")}</Button></div> : null}
    </Group>
  );
}
