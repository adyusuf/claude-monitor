import { useState, type FormEvent } from "react";
import type { Member } from "../api/types";
import type { ApplicationInput, ApplicationKind, VersioningScheme } from "../api/typesProjects";
import { Button, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { checkApplication, inputOfApplication, type ApplicationDraft } from "../lib/applicationForm";
import type { FieldErrors } from "../lib/projectForm";
import { APPLICATION_KINDS, projectLimits, VERSIONING_SCHEMES } from "../lib/projectOptions";
import { EnvironmentRows, TargetRows, TechStackEditor } from "./ApplicationRows";
import { Labeled, useFieldText, workers } from "./ProjectBits";

interface Props {
  initial: ApplicationDraft;
  members: Member[];
  submitLabel: string;
  onSubmit: (input: ApplicationInput) => Promise<void>;
  onCancel: () => void;
}

/** The definition of an application: shared by "Add application" and "Edit". Checks on the client first; the API's refusal is shown as a notice. */
export function ApplicationForm({ initial, members, submitLabel, onSubmit, onCancel }: Props) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [draft, setDraft] = useState(initial);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const err = useFieldText(errors);
  const change = (patch: Partial<ApplicationDraft>) => setDraft((d) => ({ ...d, ...patch }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const found = checkApplication(draft);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length > 0) return;
    setBusy(true);
    try {
      await onSubmit(inputOfApplication(draft));
    } catch (error) {
      setFailure(errorText(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={(e) => void submit(e)} noValidate>
      {failure ? <Notice kind="error">{failure}</Notice> : null}
      <Field label={t("projects.appName")} value={draft.name} maxLength={projectLimits.nameMax} error={err("name")} onChange={(e) => change({ name: e.target.value })} />
      <Labeled label={t("projects.description")} error={err("description")}>
        {(id) => <textarea id={id} className="input" rows={3} value={draft.description} onChange={(e) => change({ description: e.target.value })} />}
      </Labeled>
      <div className="form-grid">
        <Labeled label={t("projects.kind")} error={err("kind")}>
          {(id) => (
            <select id={id} className="input" value={draft.kind} onChange={(e) => change({ kind: e.target.value as ApplicationKind })}>
              {APPLICATION_KINDS.map((k) => <option key={k} value={k}>{t(`projects.kinds.${k}`)}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("projects.versioning")} error={err("versioningScheme")}>
          {(id) => (
            <select id={id} className="input" value={draft.versioningScheme} onChange={(e) => change({ versioningScheme: e.target.value as VersioningScheme })}>
              {VERSIONING_SCHEMES.map((v) => <option key={v} value={v}>{t(`projects.versions.${v}`)}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("projects.techOwner")} error={err("techOwnerUserId")}>
          {(id) => (
            <select id={id} className="input" value={draft.techOwnerUserId} onChange={(e) => change({ techOwnerUserId: e.target.value })}>
              <option value="">–</option>
              {workers(members, initial.techOwnerUserId).map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
            </select>
          )}
        </Labeled>
      </div>
      <Field label={t("projects.repoUrl")} value={draft.repoUrl} maxLength={projectLimits.urlMax} hint={t("projects.repoHint")} error={err("repoUrl")}
        onChange={(e) => change({ repoUrl: e.target.value })} />
      <EnvironmentRows rows={draft.environments} error={err("environments")} onChange={(environments) => change({ environments })} />
      <TechStackEditor items={draft.techStack} error={err("techStack")} onChange={(techStack) => change({ techStack })} />
      <TargetRows rows={draft.nonFunctionalTargets} error={err("nonFunctionalTargets")} onChange={(nonFunctionalTargets) => change({ nonFunctionalTargets })} />
      <div className="row-actions">
        <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
        <Button type="submit" busy={busy}>{submitLabel}</Button>
      </div>
    </form>
  );
}
