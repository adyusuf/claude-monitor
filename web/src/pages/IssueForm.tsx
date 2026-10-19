import { useState, type FormEvent } from "react";
import type { Member } from "../api/types";
import type { IssueInput, IssueStatus } from "../api/typesIssues";
import type { ApplicationSummary, ItemType, ProjectView } from "../api/typesProjects";
import { Button, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { checkIssue, inputOfIssue, type IssueDraft } from "../lib/issueForm";
import { ISSUE_STATUSES, issueLimits, PRIORITY_VALUES, TSHIRT_SIZES } from "../lib/issueOptions";
import type { FieldErrors } from "../lib/projectForm";
import { IssueBugFields } from "./IssueBugFields";
import { IssueCriteriaRows } from "./IssueCriteriaRows";
import { IssuePlacement } from "./IssuePlacement";
import { Labeled, useFieldText } from "./ProjectBits";

interface Props {
  initial: IssueDraft;
  issueId: string;
  project: ProjectView;
  members: Member[];
  applications: ApplicationSummary[];
  onSubmit: (input: IssueInput) => Promise<void>;
  onCancel: () => void;
}

/** Every field of an issue. Checks on the client first; the API's refusal is shown as a notice. The priority and the estimate follow the project's scale and unit. */
export function IssueForm({ initial, issueId, project, members, applications, onSubmit, onCancel }: Props) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [draft, setDraft] = useState(initial);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const err = useFieldText(errors);
  const change = (patch: Partial<IssueDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const types: ItemType[] = project.itemTypes.includes(initial.type) ? project.itemTypes : [initial.type, ...project.itemTypes];
  const priorities = PRIORITY_VALUES[project.priorityScale];
  const unit = project.estimationUnit;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const found = checkIssue(draft, project);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length > 0) return;
    setBusy(true);
    try {
      await onSubmit(inputOfIssue(draft));
    } catch (error) {
      setFailure(errorText(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={(e) => void submit(e)} noValidate>
      {failure ? <Notice kind="error">{failure}</Notice> : null}
      <Field label={t("issues.title")} value={draft.title} maxLength={issueLimits.titleMax} error={err("title")} onChange={(e) => change({ title: e.target.value })} />
      <div className="form-grid">
        <Labeled label={t("issues.type")} error={err("type")}>
          {(id) => (
            <select id={id} className="input" value={draft.type} onChange={(e) => change({ type: e.target.value as ItemType })}>
              {types.map((x) => <option key={x} value={x}>{t(`projects.itemType.${x}`)}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("issues.statusLabel")} error={err("status")}>
          {(id) => (
            <select id={id} className="input" value={draft.status} onChange={(e) => change({ status: e.target.value as IssueStatus })}>
              {ISSUE_STATUSES.map((s) => <option key={s} value={s}>{t(`issues.status.${s}`)}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("issues.priorityLabel")} error={err("priority")}>
          {(id) => (
            <select id={id} className="input" value={draft.priority} onChange={(e) => change({ priority: e.target.value })}>
              <option value="">{t("issues.noPriority")}</option>
              {(priorities.includes(initial.priority) || initial.priority === "" ? priorities : [initial.priority, ...priorities])
                .map((p) => <option key={p} value={p}>{tx(`issues.priority.${p}`)}</option>)}
            </select>
          )}
        </Labeled>
        {unit === "t_shirt" ? (
          <Labeled label={`${t("issues.estimate")} (${t(`projects.estimation.${unit}`)})`} error={err("estimate")}>
            {(id) => (
              <select id={id} className="input" value={draft.estimate} onChange={(e) => change({ estimate: e.target.value })}>
                <option value="">–</option>
                {TSHIRT_SIZES.map((s) => <option key={s} value={s}>{s.toUpperCase()}</option>)}
              </select>
            )}
          </Labeled>
        ) : (
          <Field label={`${t("issues.estimate")} (${t(`projects.estimation.${unit}`)})`} type="number" min={0} max={issueLimits.estimateMax} step="0.01"
            value={draft.estimate} error={err("estimate")} onChange={(e) => change({ estimate: e.target.value })} />
        )}
      </div>
      <IssuePlacement draft={draft} initial={initial} projectId={project.id} issueId={issueId} members={members} applications={applications}
        errors={err} onChange={change} />
      <Labeled label={t("issues.description")} error={err("description")}>
        {(id) => <textarea id={id} className="input" rows={4} placeholder={t("issues.descriptionHint")} value={draft.description} onChange={(e) => change({ description: e.target.value })} />}
      </Labeled>
      <IssueCriteriaRows rows={draft.acceptanceCriteria} error={err("acceptanceCriteria")} onChange={(acceptanceCriteria) => change({ acceptanceCriteria })} />
      <Labeled label={t("issues.technicalDesign")} error={err("technicalDesign")}>
        {(id) => <textarea id={id} className="input" rows={3} value={draft.technicalDesign} onChange={(e) => change({ technicalDesign: e.target.value })} />}
      </Labeled>
      {draft.type === "bug" ? <IssueBugFields draft={draft} errors={err} onChange={change} /> : null}
      <div className="row-actions">
        <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
        <Button type="submit" busy={busy}>{t("common.save")}</Button>
      </div>
    </form>
  );
}
