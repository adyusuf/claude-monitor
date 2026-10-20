import { useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { SpaceView } from "../api/typesIntegrations";
import { Button, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { IntegrationSprints } from "./IntegrationSprints";

/** Lists the spaces of the site live, imports the chosen ones as projects, and opens the sprint chooser of an imported one. */
export function IntegrationSpaces({ id, ws, canImport, onImported }: { id: string; ws: string; canImport: boolean; onImported: () => void }) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [spaces, setSpaces] = useState<SpaceView[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [sprintsOf, setSprintsOf] = useState<SpaceView | null>(null);
  const [notices, setNotices] = useState<{ kind: "error" | "success"; text: string }[]>([]);

  const load = async () => {
    setLoading(true);
    try {
      setSpaces(await api.integrationSpaces(id));
    } catch (e) {
      setNotices([{ kind: "error", text: errorText(e) }]);
    } finally {
      setLoading(false);
    }
  };

  const toggle = (key: string) => {
    const next = new Set(picked);
    if (!next.delete(key)) next.add(key);
    setPicked(next);
  };

  const run = async () => {
    setBusy(true);
    try {
      const results = await api.importSpaces(id, [...picked]);
      setNotices(results.map((r) => r.outcome === "ok"
        ? { kind: "success" as const, text: t("integrations.importResult", { key: r.key, created: r.created, updated: r.updated }) }
        : { kind: "error" as const, text: t("integrations.importProblem", { key: r.key, error: tx(`errors.${r.outcome}`) }) }));
      setPicked(new Set());
      await load();
      onImported();
    } catch (e) {
      setNotices([{ kind: "error", text: errorText(e) }]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      <p className="muted">{t("integrations.spacesHint")}</p>
      {notices.map((n, i) => <Notice key={i} kind={n.kind}>{n.text}</Notice>)}
      {!spaces ? <Button variant="ghost" busy={loading} onClick={() => void load()}>{t("integrations.loadSpaces")}</Button> : loading ? <Spinner /> : spaces.length === 0 ? <p className="muted">{t("integrations.noSpaces")}</p> : (
        <ul className="plain">
          {spaces.map((s) => (
            <li key={s.key} className="check">
              <input type="checkbox" aria-label={t("integrations.selectSpace", { name: s.name })} checked={picked.has(s.key)} disabled={!canImport} onChange={() => toggle(s.key)} />
              <span className="grow">
                <strong>{s.name}</strong> <span className="muted">{s.key}</span>{" "}
                {s.imported && s.projectId ? (
                  <>
                    <Link to={`/w/${ws}/projects/${s.projectId}`}>{t("integrations.openProject")}</Link>{" "}
                    <Button variant="subtle" onClick={() => setSprintsOf(s)}>{t("integrations.sprintsOf")}</Button>
                  </>
                ) : null}
              </span>
            </li>
          ))}
        </ul>
      )}
      {spaces && canImport ? <Button busy={busy} disabled={picked.size === 0} onClick={() => void run()}>{t("integrations.importSelected", { n: picked.size })}</Button> : null}
      {sprintsOf ? <IntegrationSprints id={id} spaceKey={sprintsOf.key} name={sprintsOf.name} ws={ws} projectId={sprintsOf.projectId} onClose={() => setSprintsOf(null)} /> : null}
    </div>
  );
}
