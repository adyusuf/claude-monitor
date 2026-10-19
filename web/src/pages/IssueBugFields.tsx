import type { Severity } from "../api/typesIssues";
import type { IssueDraft } from "../lib/issueForm";
import { useI18n } from "../i18n";
import { SEVERITIES } from "../lib/issueOptions";
import { Group, Labeled } from "./ProjectBits";

/** The part of the form only a bug has: where it happens, how to see it, what was expected, what happened and how bad it is. */
export function IssueBugFields({ draft, errors, onChange }: {
  draft: IssueDraft; errors: (field: string) => string | undefined; onChange: (patch: Partial<IssueDraft>) => void;
}) {
  const { t } = useI18n();
  const area = (label: string, field: "bugEnvironment" | "reproSteps" | "expectedResult" | "actualResult") => (
    <Labeled label={label} error={errors(field)}>
      {(id) => <textarea id={id} className="input" rows={3} value={draft[field]} onChange={(e) => onChange({ [field]: e.target.value })} />}
    </Labeled>
  );
  return (
    <Group legend={t("issues.bugSection")}>
      {area(t("issues.bugEnvironment"), "bugEnvironment")}
      {area(t("issues.reproSteps"), "reproSteps")}
      {area(t("issues.expectedResult"), "expectedResult")}
      {area(t("issues.actualResult"), "actualResult")}
      <Labeled label={t("issues.severity")} error={errors("severity")}>
        {(id) => (
          <select id={id} className="input" value={draft.severity} onChange={(e) => onChange({ severity: e.target.value as Severity | "" })}>
            <option value="">–</option>
            {SEVERITIES.map((s) => <option key={s} value={s}>{t(`issues.severities.${s}`)}</option>)}
          </select>
        )}
      </Labeled>
    </Group>
  );
}
