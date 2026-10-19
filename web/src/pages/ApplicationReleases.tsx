import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ReleaseStatus, ReleaseView } from "../api/typesProjects";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { day } from "../lib/format";
import { projectLimits, RELEASE_STATUSES } from "../lib/projectOptions";

interface Draft { name: string; plannedDate: string; status: ReleaseStatus }
const blank: Draft = { name: "", plannedDate: "", status: "planned" };

/** The release calendar of an application: name or version, planned day and state. A planned release is cancelled, never deleted. */
export function ApplicationReleases({ applicationId, canEdit }: { applicationId: string; canEdit: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<ReleaseView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft>(blank);
  const [editing, setEditing] = useState<{ id: string; draft: Draft } | null>(null);

  const load = useCallback(async () => {
    try {
      setRows(await api.releases(applicationId));
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

  const input = (d: Draft) => ({ name: d.name, plannedDate: d.plannedDate || null, status: d.status });

  const fields = (d: Draft, label: string, onChange: (d: Draft) => void) => (
    <>
      <input className="input" aria-label={`${t("projects.releaseName")} ${label}`} maxLength={projectLimits.nameMax} value={d.name} onChange={(e) => onChange({ ...d, name: e.target.value })} />
      <input className="input" type="date" aria-label={`${t("projects.plannedDate")} ${label}`} value={d.plannedDate} onChange={(e) => onChange({ ...d, plannedDate: e.target.value })} />
      <select className="input" aria-label={`${t("projects.releaseStatus")} ${label}`} value={d.status} onChange={(e) => onChange({ ...d, status: e.target.value as ReleaseStatus })}>
        {RELEASE_STATUSES.map((s) => <option key={s} value={s}>{t(`projects.release.${s}`)}</option>)}
      </select>
    </>
  );

  return (
    <Card title={t("projects.releases")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <p className="muted">{t("projects.noReleases")}</p> : (
        <ul className="plain">
          {rows.map((r) => (
            <li key={r.id} className="spread">
              {editing?.id === r.id ? (
                <form className="row-input grow" onSubmit={(e) => {
                  e.preventDefault();
                  if (!blankName(editing.draft.name)) void act(() => api.putRelease(r.id, input(editing.draft)), () => setEditing(null));
                }}>
                  {fields(editing.draft, r.name, (d) => setEditing({ id: r.id, draft: d }))}
                  <Button type="submit">{t("common.save")}</Button>
                  <Button variant="ghost" onClick={() => setEditing(null)}>{t("common.cancel")}</Button>
                </form>
              ) : (
                <>
                  <span>
                    <strong>{r.name}</strong> <span className={`badge ${r.status === "released" ? "ok" : ""}`}>{t(`projects.release.${r.status}`)}</span>
                    {r.plannedDate ? <span className="muted small"> · {day(r.plannedDate)}</span> : null}
                  </span>
                  {canEdit ? (
                    <span className="row-actions">
                      <Button variant="ghost" onClick={() => setEditing({ id: r.id, draft: { name: r.name, plannedDate: r.plannedDate ?? "", status: r.status } })}>{t("projects.edit")}</Button>
                      {r.status === "released" ? null : <Button variant="ghost" onClick={() => void act(() => api.cancelRelease(r.id))}>{t("projects.cancelRelease")}</Button>}
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
          if (!blankName(draft.name)) void act(() => api.addRelease(applicationId, input(draft)), () => setDraft(blank));
        }}>
          {fields(draft, t("projects.newRelease"), setDraft)}
          <Button type="submit">{t("projects.addRelease")}</Button>
        </form>
      ) : null}
    </Card>
  );
}
