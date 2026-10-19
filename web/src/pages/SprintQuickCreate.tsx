import { useState, type FormEvent } from "react";
import { api } from "../api/endpoints";
import type { ItemType } from "../api/typesProjects";
import type { SprintSummary } from "../api/typesSprints";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { issueLimits } from "../lib/issueOptions";

/**
 * A title and a type make an issue that goes straight into the sprint: the issue is created (for an application's sprint, in that application),
 * then added. If the adding is refused the issue stays in the backlog and the refusal is shown.
 */
export function SprintQuickCreate({ sprint, itemTypes, onChanged }: { sprint: SprintSummary; itemTypes: ItemType[]; onChanged: () => Promise<void> }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [title, setTitle] = useState("");
  const [type, setType] = useState<ItemType>(itemTypes[0] ?? "story");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (title.trim() === "") return setError(t("errors.invalid_name"));
    setBusy(true);
    try {
      setError(null);
      const created = await api.createIssue(sprint.projectId, { title: title.trim(), type, applicationId: sprint.applicationId });
      try {
        await api.addSprintIssues(sprint.id, [created.issue.id]);
        setTitle("");
      } finally {
        await onChanged();
      }
    } catch (failure) {
      setError(errorText(failure));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={(e) => void submit(e)} noValidate>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <div className="row-input">
        <input className="input" aria-label={t("issues.newTitle")} placeholder={t("issues.newTitle")} maxLength={issueLimits.titleMax} value={title}
          onChange={(e) => setTitle(e.target.value)} />
        <select className="input type-pick" aria-label={t("issues.newType")} value={type} onChange={(e) => setType(e.target.value as ItemType)}>
          {itemTypes.map((x) => <option key={x} value={x}>{t(`projects.itemType.${x}`)}</option>)}
        </select>
        <Button type="submit" busy={busy}>{t("sprints.createAndAdd")}</Button>
      </div>
    </form>
  );
}
