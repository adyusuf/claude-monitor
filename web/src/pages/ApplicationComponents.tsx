import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ComponentKind, ComponentView } from "../api/typesProjects";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { COMPONENT_KINDS, projectLimits } from "../lib/projectOptions";

interface Draft { name: string; kind: ComponentKind }
const blank: Draft = { name: "", kind: "component" };

/** The components and modules of an application: add, rename and archive (the ones issues will be tagged with later). */
export function ApplicationComponents({ applicationId, canEdit }: { applicationId: string; canEdit: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<ComponentView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>(blank);
  const [editing, setEditing] = useState<{ id: string; draft: Draft } | null>(null);

  const load = useCallback(async () => {
    try {
      setRows(await api.components(applicationId));
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [applicationId]);

  useEffect(() => {
    void load();
  }, [load]);

  const blankName = (name: string) => {
    if (name.trim() !== "") return false;
    setError(t("errors.invalid_name"));
    return true;
  };

  const act = async (work: () => Promise<unknown>, done?: () => void) => {
    try {
      setError(null);
      await work();
      done?.();
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  const kindSelect = (value: ComponentKind, label: string, onChange: (kind: ComponentKind) => void) => (
    <select className="input" aria-label={label} value={value} onChange={(e) => onChange(e.target.value as ComponentKind)}>
      {COMPONENT_KINDS.map((k) => <option key={k} value={k}>{t(`projects.componentKinds.${k}`)}</option>)}
    </select>
  );

  return (
    <Card title={t("projects.components")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <p className="muted">{t("projects.noComponents")}</p> : (
        <ul className="plain">
          {rows.map((c) => (
            <li key={c.id} className="spread">
              {editing?.id === c.id ? (
                <form className="row-input grow" onSubmit={(e) => {
                  e.preventDefault();
                  if (!blankName(editing.draft.name)) void act(() => api.putComponent(c.id, editing.draft), () => setEditing(null));
                }}>
                  <input className="input" aria-label={t("projects.componentName")} maxLength={projectLimits.nameMax} value={editing.draft.name}
                    onChange={(e) => setEditing({ id: c.id, draft: { ...editing.draft, name: e.target.value } })} />
                  {kindSelect(editing.draft.kind, t("projects.componentKind"), (kind) => setEditing({ id: c.id, draft: { ...editing.draft, kind } }))}
                  <Button type="submit">{t("common.save")}</Button>
                  <Button variant="ghost" onClick={() => setEditing(null)}>{t("common.cancel")}</Button>
                </form>
              ) : (
                <>
                  <span><strong>{c.name}</strong> <span className="badge">{t(`projects.componentKinds.${c.kind}`)}</span></span>
                  {canEdit ? (
                    <span className="row-actions">
                      <Button variant="ghost" onClick={() => setEditing({ id: c.id, draft: { name: c.name, kind: c.kind } })}>{t("projects.rename")}</Button>
                      <Button variant="ghost" onClick={() => void act(() => api.archiveComponent(c.id))}>{t("projects.archive")}</Button>
                    </span>
                  ) : null}
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      {canEdit ? (
        <form className="row-input add-row" onSubmit={(e) => {
          e.preventDefault();
          if (!blankName(draft.name)) void act(() => api.addComponent(applicationId, draft), () => setDraft(blank));
        }}>
          <input className="input" aria-label={t("projects.newComponentName")} maxLength={projectLimits.nameMax} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          {kindSelect(draft.kind, t("projects.newComponentKind"), (kind) => setDraft({ ...draft, kind }))}
          <Button type="submit">{t("projects.addComponent")}</Button>
        </form>
      ) : null}
    </Card>
  );
}
