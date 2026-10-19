import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import type { CriterionDraft } from "../lib/issueForm";
import { issueLimits } from "../lib/issueOptions";
import { Group } from "./ProjectBits";

/** Acceptance criteria as Given / When / Then rows, added and removed one by one (at most 30). */
export function IssueCriteriaRows({ rows, onChange, error }: { rows: CriterionDraft[]; onChange: (rows: CriterionDraft[]) => void; error?: string }) {
  const { t } = useI18n();
  const part = (i: number, key: keyof CriterionDraft, label: string) => (
    <input className="input" aria-label={`${label} ${i + 1}`} placeholder={label} maxLength={issueLimits.lineMax} value={rows[i]![key]}
      onChange={(e) => onChange(rows.map((r, j) => j === i ? { ...r, [key]: e.target.value } : r))} />
  );
  return (
    <Group legend={t("issues.criteria")} error={error}>
      {rows.map((_, i) => (
        <div key={i} className="row-input criterion">
          {part(i, "given", t("issues.given"))}
          {part(i, "when", t("issues.when"))}
          {part(i, "then", t("issues.then"))}
          <Button variant="subtle" aria-label={`${t("projects.removeRow")} ${t("issues.criteria")} ${i + 1}`} onClick={() => onChange(rows.filter((_, j) => j !== i))}>×</Button>
        </div>
      ))}
      {rows.length < issueLimits.criteriaMax
        ? <div><Button variant="ghost" onClick={() => onChange([...rows, { given: "", when: "", then: "" }])}>{t("issues.addCriterion")}</Button></div> : null}
    </Group>
  );
}
