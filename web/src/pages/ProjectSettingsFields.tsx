import type { EstimationUnit, ItemType, PriorityScale } from "../api/typesProjects";
import { Field } from "../components/ui";
import { useI18n } from "../i18n";
import type { FieldErrors, ProjectDraft } from "../lib/projectForm";
import { ESTIMATION_UNITS, ITEM_TYPES, PRIORITY_SCALES, projectLimits } from "../lib/projectOptions";
import { Group, Labeled, LineList, useFieldText } from "./ProjectBits";

/** The settings of a project that its levels below inherit: item types, scales, sprint length and the two definitions. */
export function ProjectSettingsFields({ draft, errors, onChange }: { draft: ProjectDraft; errors: FieldErrors; onChange: (patch: Partial<ProjectDraft>) => void }) {
  const { t } = useI18n();
  const err = useFieldText(errors);
  const toggle = (type: ItemType) =>
    onChange({ itemTypes: draft.itemTypes.includes(type) ? draft.itemTypes.filter((x) => x !== type) : ITEM_TYPES.filter((x) => x === type || draft.itemTypes.includes(x)) });
  return (
    <>
      <Group legend={t("projects.itemTypes")} error={err("itemTypes")}>
        <div className="check-row">
          {ITEM_TYPES.map((type) => (
            <label key={type} className="check">
              <input type="checkbox" checked={draft.itemTypes.includes(type)} onChange={() => toggle(type)} />
              <span>{t(`projects.itemType.${type}`)}</span>
            </label>
          ))}
        </div>
      </Group>
      <div className="form-grid">
        <Labeled label={t("projects.priorityScale")} error={err("priorityScale")}>
          {(id) => (
            <select id={id} className="input" value={draft.priorityScale} onChange={(e) => onChange({ priorityScale: e.target.value as PriorityScale })}>
              {PRIORITY_SCALES.map((s) => <option key={s} value={s}>{t(`projects.priority.${s}`)}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("projects.estimationUnit")} error={err("estimationUnit")}>
          {(id) => (
            <select id={id} className="input" value={draft.estimationUnit} onChange={(e) => onChange({ estimationUnit: e.target.value as EstimationUnit })}>
              {ESTIMATION_UNITS.map((u) => <option key={u} value={u}>{t(`projects.estimation.${u}`)}</option>)}
            </select>
          )}
        </Labeled>
        <Field label={t("projects.sprintLength")} type="number" min={projectLimits.sprintMin} max={projectLimits.sprintMax} value={draft.sprintLengthDays}
          error={err("sprintLengthDays")} onChange={(e) => onChange({ sprintLengthDays: e.target.value })} />
      </div>
      <LineList label={t("projects.definitionOfReady")} lines={draft.definitionOfReady} max={projectLimits.listMax} error={err("definitionOfReady")}
        onChange={(definitionOfReady) => onChange({ definitionOfReady })} />
      <LineList label={t("projects.definitionOfDone")} lines={draft.definitionOfDone} max={projectLimits.listMax} error={err("definitionOfDone")}
        onChange={(definitionOfDone) => onChange({ definitionOfDone })} />
    </>
  );
}
