import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router";
import { api } from "../api/endpoints";
import type { ItemType } from "../api/typesProjects";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { issueLimits } from "../lib/issueOptions";

/** A title and a type make an issue; it opens on its own page so the rest can be filled in. A new issue takes the project's defaults for the other fields. */
export function BacklogQuickAdd({ ws, projectId, itemTypes, applicationId }: {
  ws: string; projectId: string; itemTypes: ItemType[]; applicationId: string | null;
}) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const navigate = useNavigate();
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
      const created = await api.createIssue(projectId, { title: title.trim(), type, applicationId });
      navigate(`/w/${ws}/projects/${projectId}/issues/${created.issue.id}`);
    } catch (failure) {
      setError(errorText(failure));
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
        <Button type="submit" busy={busy}>{t("issues.add")}</Button>
      </div>
    </form>
  );
}
