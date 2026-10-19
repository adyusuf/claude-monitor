import { useState, type FormEvent } from "react";
import type { Member } from "../api/types";
import type { ProjectView } from "../api/typesProjects";
import { Button, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import type { FieldErrors } from "../lib/projectForm";
import { checkSprint, type SprintDraft } from "../lib/sprintForm";
import { addDays, sprintLimits } from "../lib/sprintOptions";
import { Group, Labeled, useFieldText } from "./ProjectBits";
import type { ReleaseOption } from "./useSprintReleases";

interface Props {
  initial: SprintDraft;
  project: ProjectView;
  /** The people who can be in the team (workspace members who are not viewers, plus whoever already is). */
  people: Member[];
  releases: ReleaseOption[];
  submitLabel: string;
  onSubmit: (draft: SprintDraft) => Promise<void>;
  onCancel: () => void;
}

/** The fields of a sprint, for a new one and for changing one. Checks on the client first; the API's refusal is shown as a notice. */
export function SprintForm({ initial, project, people, releases, submitLabel, onSubmit, onCancel }: Props) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [draft, setDraft] = useState(initial);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const err = useFieldText(errors);
  const change = (patch: Partial<SprintDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const unit = project.estimationUnit;

  /** A new start day drags the end day along while the end still is the default length after the old start. */
  const moveStart = (startDate: string) => setDraft((d) => ({
    ...d, startDate, endDate: d.endDate === addDays(d.startDate, project.sprintLengthDays) ? addDays(startDate, project.sprintLengthDays) : d.endDate,
  }));
  const toggle = (id: string) => change({ teamUserIds: draft.teamUserIds.includes(id) ? draft.teamUserIds.filter((x) => x !== id) : [...draft.teamUserIds, id] });

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const found = checkSprint(draft);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length > 0) return;
    setBusy(true);
    try {
      await onSubmit(draft);
    } catch (error) {
      setFailure(errorText(error));
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={(e) => void submit(e)} noValidate>
      {failure ? <Notice kind="error">{failure}</Notice> : null}
      <Field label={t("sprints.name")} value={draft.name} maxLength={sprintLimits.nameMax} error={err("name")} onChange={(e) => change({ name: e.target.value })} />
      <Labeled label={t("sprints.goal")} error={err("goal")}>
        {(id) => <textarea id={id} className="input" rows={2} maxLength={sprintLimits.goalMax} value={draft.goal} onChange={(e) => change({ goal: e.target.value })} />}
      </Labeled>
      <div className="form-grid">
        <Field label={t("sprints.startDate")} type="date" value={draft.startDate} error={err("startDate")} onChange={(e) => moveStart(e.target.value)} />
        <Field label={t("sprints.endDate")} type="date" value={draft.endDate} error={err("endDate")} onChange={(e) => change({ endDate: e.target.value })} />
        <Field label={unit === "t_shirt" ? t("sprints.capacity") : t("sprints.capacityIn", { unit: t(`projects.estimation.${unit}`) })} type="number" min={0}
          step="0.01" value={draft.capacity} error={err("capacity")} onChange={(e) => change({ capacity: e.target.value })} />
        <Labeled label={t("sprints.release")} error={err("releaseId")}>
          {(id) => (
            <select id={id} className="input" value={draft.releaseId} onChange={(e) => change({ releaseId: e.target.value })}>
              <option value="">{t("sprints.noRelease")}</option>
              {releases.map((r) => <option key={r.id} value={r.id}>{r.label}</option>)}
            </select>
          )}
        </Labeled>
      </div>
      <Group legend={t("sprints.team")} error={err("teamUserIds")}>
        <div className="check-row">
          {people.map((m) => (
            <label key={m.userId} className="check">
              <input type="checkbox" checked={draft.teamUserIds.includes(m.userId)} onChange={() => toggle(m.userId)} />
              <span>{m.displayName}</span>
            </label>
          ))}
        </div>
      </Group>
      <div className="row-actions">
        <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
        <Button type="submit" busy={busy}>{submitLabel}</Button>
      </div>
    </form>
  );
}
