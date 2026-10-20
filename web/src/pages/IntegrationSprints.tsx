import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { SprintChoice } from "../api/typesIntegrations";
import { Button, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { date } from "../lib/format";

/** The sprints of one imported space, live from the tool: pick the ones to bring in with their issues. */
export function IntegrationSprints({ id, spaceKey, name, ws, projectId, onClose }:
  { id: string; spaceKey: string; name: string; ws: string; projectId: string | null; onClose: () => void }) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [sprints, setSprints] = useState<SprintChoice[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<{ kind: "error" | "success"; text: string } | null>(null);

  const load = () => api.integrationSprints(id, spaceKey).then(setSprints).catch((e) => setNotice({ kind: "error", text: errorText(e) }));
  useEffect(() => {
    void load();
    // load closes over errorText, which is a fresh function each render
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, spaceKey]);

  const toggle = (sprintId: string) => {
    const next = new Set(picked);
    if (!next.delete(sprintId)) next.add(sprintId);
    setPicked(next);
  };

  const run = async () => {
    setBusy(true);
    try {
      const results = await api.importSprints(id, spaceKey, [...picked]);
      const bad = results.find((r) => r.outcome !== "ok");
      setNotice(bad ? { kind: "error", text: tx(`errors.${bad.outcome}`) } : { kind: "success", text: t("integrations.result.ok") });
      setPicked(new Set());
      await load();
    } catch (e) {
      setNotice({ kind: "error", text: errorText(e) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="subpanel">
      <div className="row-input">
        <h3 className="grow">{t("integrations.sprintsTitle", { name })}</h3>
        <Button variant="ghost" onClick={onClose}>{t("integrations.close")}</Button>
      </div>
      <p className="muted">{t("integrations.sprintsHint")}</p>
      {notice ? <Notice kind={notice.kind}>{notice.text}</Notice> : null}
      {!sprints ? <Spinner /> : sprints.length === 0 ? <p className="muted">{t("integrations.noSprints")}</p> : (
        <ul className="plain">
          {sprints.map((s) => (
            <li key={s.id} className="check">
              <input type="checkbox" aria-label={t("integrations.selectSprint", { name: s.name })} checked={picked.has(s.id)} disabled={s.imported} onChange={() => toggle(s.id)} />
              <span>
                {s.name} <span className="badge">{t(`integrations.sprintState.${s.state}`)}</span>{" "}
                {s.start || s.end ? <span className="muted">{date(s.start)} – {date(s.end)}</span> : null}{" "}
                {s.imported && projectId ? <Link to={`/w/${ws}/projects/${projectId}/sprints/${s.sprintId}`}>{t("integrations.imported")}</Link> : null}
              </span>
            </li>
          ))}
        </ul>
      )}
      <Button busy={busy} disabled={picked.size === 0} onClick={() => void run()}>{t("integrations.importSprints", { n: picked.size })}</Button>
    </div>
  );
}
