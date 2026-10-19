import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { IssueSummary } from "../api/typesIssues";
import { Button, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { issueLimits } from "../lib/issueOptions";
import { useRunErrorText } from "./IssueRunBits";
import { useRunProjects } from "./useRunProjects";

/** Links a run to an issue: pick a project, search its issues, pick one. The API refuses a repeat and a project the person cannot work in. */
export function RunIssueLinkForm({ ws, runId, onLinked, onCancel }: { ws: string; runId: string; onLinked: () => void; onCancel: () => void }) {
  const { t } = useI18n();
  const errorText = useRunErrorText();
  const { projects, error: loadError } = useRunProjects(ws, false);
  const [projectId, setProjectId] = useState("");
  const [search, setSearch] = useState("");
  const [found, setFound] = useState<IssueSummary[]>([]);
  const [issueId, setIssueId] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!projectId) return;
    let live = true;
    const text = search.trim();
    const timer = setTimeout(() => {
      api.issues(projectId, { q: text === "" ? undefined : text, limit: issueLimits.searchLimit }).then(
        (page) => live && setFound(page.items),
        (e: unknown) => live && setError(errorText(e)),
      );
    }, text === "" ? 0 : config.liveDebounceMs);
    return () => {
      live = false;
      clearTimeout(timer);
    };
    // errorText is a fresh function each render; searching again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, search]);

  const link = async () => {
    try {
      setError(null);
      await api.linkIssueRun(issueId, runId);
      onLinked();
    } catch (e) {
      setError(errorText(e));
    }
  };

  if (loadError) return <Notice kind="error">{errorText(loadError)}</Notice>;
  if (projects === null) return <Spinner />;
  if (projects.length === 0) return <p className="muted">{t("issues.runNoProjects")}</p>;
  const shown = projectId ? found : [];
  return (
    <>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <form className="row-input grow" onSubmit={(e) => {
        e.preventDefault();
        if (issueId) void link();
      }}>
        <select className="input" aria-label={t("issues.runProject")} value={projectId} onChange={(e) => {
          setProjectId(e.target.value);
          setFound([]);
          setIssueId("");
        }}>
          <option value="">–</option>
          {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <input className="input" type="search" aria-label={t("issues.searchToLink")} placeholder={t("issues.searchToLink")} value={search} disabled={!projectId}
          onChange={(e) => setSearch(e.target.value)} />
        <select className="input" aria-label={t("issues.linkTarget")} value={issueId} disabled={!projectId} onChange={(e) => setIssueId(e.target.value)}>
          <option value="">{projectId && shown.length === 0 ? t("issues.noMatch") : "–"}</option>
          {shown.map((x) => <option key={x.id} value={x.id}>{`#${x.number} ${x.title}`}</option>)}
        </select>
        <Button type="submit" disabled={!issueId}>{t("issues.runLinkAction")}</Button>
        <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
      </form>
    </>
  );
}
