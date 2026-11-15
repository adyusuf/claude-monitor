import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { SprintChoice } from "../api/typesIntegrations";
import { Button, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { date } from "../lib/format";

/** The sprints of one imported space, live from the tool: pick the ones to bring in with their issues. */
export function IntegrationSprints({ id, spaceKey, name, ws, projectId, canImport, onClose }:
  { id: string; spaceKey: string; name: string; ws: string; projectId: string | null; canImport: boolean; onClose: () => void }) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [sprints, setSprints] = useState<SprintChoice[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [notices, setNotices] = useState<{ kind: "error" | "success"; text: string }[]>([]);

  const load = () => api.integrationSprints(id, spaceKey).then(setSprints).catch((e) => setNotices([{ kind: "error", text: errorText(e) }]));
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
      const nameOf = (sprintId: string) => sprints?.find((s) => s.id === sprintId)?.name ?? sprintId;
      // All done is one line; otherwise a line per sprint, so what did import is not read as a plain failure.
      setNotices(results.every((r) => r.outcome === "ok")
        ? [{ kind: "success", text: t("integrations.result.ok") }]
        : results.map((r) => r.outcome === "ok"
          ? { kind: "success" as const, text: t("integrations.sprintImported", { name: nameOf(r.sprintId) }) }
          : { kind: "error" as const, text: t("integrations.importProblem", { key: nameOf(r.sprintId), error: tx(`errors.${r.outcome}`) }) }));
      setPicked(new Set());
      await load();
    } catch (e) {
      setNotices([{ kind: "error", text: errorText(e) }]);
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
      {notices.map((n, i) => <Notice key={i} kind={n.kind}>{n.text}</Notice>)}
      {!sprints ? <Spinner /> : sprints.length === 0 ? <p className="muted">{t("integrations.noSprints")}</p> : (
        <ul className="plain">
          {sprints.map((s) => (
            <li key={s.id} className="check">
              <input type="checkbox" aria-label={t("integrations.selectSprint", { name: s.name })} checked={picked.has(s.id)} disabled={s.imported || !canImport} onChange={() => toggle(s.id)} />
              <span>
                {s.name} <span className="badge">{t(`integrations.sprintState.${s.state}`)}</span>{" "}
                {s.start || s.end ? <span className="muted">{date(s.start)} – {date(s.end)}</span> : null}{" "}
                {s.imported && projectId ? <Link to={`/w/${ws}/projects/${projectId}/sprints/${s.sprintId}`}>{t("integrations.imported")}</Link> : null}
              </span>
            </li>
          ))}
        </ul>
      )}
      {canImport ? <Button busy={busy} disabled={picked.size === 0} onClick={() => void run()}>{t("integrations.importSprints", { n: picked.size })}</Button> : null}
    </div>
  );
}
