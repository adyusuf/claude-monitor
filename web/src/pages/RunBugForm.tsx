import { useRef, useState } from "react";
import { useNavigate } from "react-router";
import { api } from "../api/endpoints";
import type { WebRunView } from "../api/types";
import { Button, Notice, Spinner } from "../components/ui";
import { useI18n } from "../i18n";
import { issueLimits } from "../lib/issueOptions";
import { useRunErrorText } from "./IssueRunBits";
import { useRunProjects } from "./useRunProjects";

/**
 * Creates a bug for a failed run and links the run to it. The project list holds only projects that take bugs; the title is prefilled and editable.
 * The bug carries the title alone: never the run's command or output. A bug that was made but could not be linked is kept, so a retry only links.
 */
export function RunBugForm({ run, ws, onCancel }: { run: WebRunView; ws: string; onCancel: () => void }) {
  const { t } = useI18n();
  const errorText = useRunErrorText();
  const navigate = useNavigate();
  const { projects, error: loadError } = useRunProjects(ws, true);
  const [projectId, setProjectId] = useState("");
  const [title, setTitle] = useState(() => ((run.testsFailed ?? 0) > 0
    ? t("issues.runBugTitleTests", { host: run.targetHostname, n: run.testsFailed ?? 0 })
    : t("issues.runBugTitleExit", { host: run.targetHostname, code: run.exitCode ?? "–" })).slice(0, issueLimits.titleMax));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const made = useRef<{ projectId: string; issueId: string } | null>(null);

  const create = async () => {
    try {
      setBusy(true);
      setError(null);
      if (!made.current || made.current.projectId !== projectId) {
        const created = await api.createIssue(projectId, { title: title.trim(), type: "bug", status: "backlog" });
        made.current = { projectId, issueId: created.issue.id };
      }
      await api.linkIssueRun(made.current.issueId, run.id);
      navigate(`/w/${ws}/projects/${projectId}/issues/${made.current.issueId}`);
    } catch (e) {
      setError(errorText(e));
      setBusy(false);
    }
  };

  if (loadError) return <Notice kind="error">{errorText(loadError)}</Notice>;
  if (projects === null) return <Spinner />;
  if (projects.length === 0) return <p className="muted">{t("issues.runNoBugProject")}</p>;
  return (
    <>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <form className="row-input grow" onSubmit={(e) => {
        e.preventDefault();
        if (projectId && title.trim()) void create();
      }}>
        <select className="input kind-pick" aria-label={t("issues.runProject")} value={projectId} disabled={busy} onChange={(e) => setProjectId(e.target.value)}>
          <option value="">–</option>
          {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <input className="input" aria-label={t("issues.runBugTitle")} maxLength={issueLimits.titleMax} value={title} disabled={busy} onChange={(e) => setTitle(e.target.value)} />
        <Button type="submit" busy={busy} disabled={!projectId || !title.trim()}>{t("issues.runCreateAction")}</Button>
        <Button variant="ghost" disabled={busy} onClick={onCancel}>{t("common.cancel")}</Button>
      </form>
      <p className="muted small">{t("issues.runBugNote")}</p>
    </>
  );
}
