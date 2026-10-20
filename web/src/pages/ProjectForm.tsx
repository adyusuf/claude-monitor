import { useState, type FormEvent } from "react";
import { Button, Field, Notice } from "../components/ui";
import type { ProjectInput } from "../api/typesProjects";
import type { Member } from "../api/types";
import { useErrorText, useI18n } from "../i18n";
import { checkProject, inputOfProject, type FieldErrors, type ProjectDraft } from "../lib/projectForm";
import { projectLimits } from "../lib/projectOptions";
import { Labeled, useFieldText, workers } from "./ProjectBits";
import { ProjectSettingsFields } from "./ProjectSettingsFields";
import { StakeholderRows } from "./StakeholderRows";

interface Props {
  initial: ProjectDraft;
  members: Member[];
  /** Only the owner and workspace admins may hand a project to somebody else. */
  canChangeOwner: boolean;
  submitLabel: string;
  /** A project that mirrors a space in a work tracker keeps its name and purpose there (ADR-0015 §7). */
  mirrored?: boolean;
  onSubmit: (input: ProjectInput) => Promise<void>;
  onCancel: () => void;
}

/** The definition of a project: shared by "New project" and "Edit". Checks on the client first; the API's refusal is shown as a notice. */
export function ProjectForm({ initial, members, canChangeOwner, submitLabel, mirrored = false, onSubmit, onCancel }: Props) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [draft, setDraft] = useState(initial);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const err = useFieldText(errors);
  const change = (patch: Partial<ProjectDraft>) => setDraft((d) => ({ ...d, ...patch }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const found = checkProject(draft);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length > 0) return;
    setBusy(true);
    try {
      await onSubmit(inputOfProject(draft));
    } catch (error) {
      setFailure(errorText(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={(e) => void submit(e)} noValidate>
      {failure ? <Notice kind="error">{failure}</Notice> : null}
      <Field label={t("projects.name")} value={draft.name} maxLength={projectLimits.nameMax} error={err("name")} disabled={mirrored} onChange={(e) => change({ name: e.target.value })} />
      <Labeled label={t("projects.purpose")} error={err("purpose")}>
        {(id) => <textarea id={id} className="input" rows={3} value={draft.purpose} disabled={mirrored} onChange={(e) => change({ purpose: e.target.value })} />}
      </Labeled>
      <div className="form-grid">
        <Labeled label={t("projects.owner")} error={err("ownerUserId")}>
          {(id) => (
            <select id={id} className="input" value={draft.ownerUserId} disabled={!canChangeOwner} onChange={(e) => change({ ownerUserId: e.target.value })}>
              {workers(members, initial.ownerUserId).map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
            </select>
          )}
        </Labeled>
        <Field label={t("projects.sponsor")} value={draft.sponsor} maxLength={projectLimits.sponsorMax} error={err("sponsor")} onChange={(e) => change({ sponsor: e.target.value })} />
      </div>
      <StakeholderRows rows={draft.stakeholders} error={err("stakeholders")} onChange={(stakeholders) => change({ stakeholders })} />
      <div className="form-grid">
        <Field label={t("projects.budgetAmount")} type="number" min={0} value={draft.budgetAmount} error={err("budget") ?? err("budgetAmount")}
          onChange={(e) => change({ budgetAmount: e.target.value })} />
        <Field label={t("projects.budgetCurrency")} value={draft.budgetCurrency} maxLength={3} placeholder="EUR" hint={t("projects.currencyHint")} error={err("budgetCurrency")}
          onChange={(e) => change({ budgetCurrency: e.target.value.toUpperCase() })} />
        <Field label={t("projects.startDate")} type="date" value={draft.startDate} error={err("startDate")} onChange={(e) => change({ startDate: e.target.value })} />
        <Field label={t("projects.endDate")} type="date" value={draft.endDate} error={err("endDate")} onChange={(e) => change({ endDate: e.target.value })} />
      </div>
      <ProjectSettingsFields draft={draft} errors={errors} onChange={change} />
      <div className="row-actions">
        <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
        <Button type="submit" busy={busy}>{submitLabel}</Button>
      </div>
    </form>
  );
}
