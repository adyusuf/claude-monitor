import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { RunIssueView } from "../api/typesIssues";
import type { WebRunView } from "../api/types";
import { Button, Notice, Spinner } from "../components/ui";
import { useI18n } from "../i18n";
import { runFailed } from "../lib/issueRuns";
import { StatusBadge } from "./IssueBits";
import { useRunErrorText } from "./IssueRunBits";
import { RunBugForm } from "./RunBugForm";
import { RunIssueLinkForm } from "./RunIssueLinkForm";

type Action = "link" | "bug" | null;

/** The issues a finished run is linked to (read when this opens), with the ways to link it to one more: an existing issue or a new bug (offered for a failed run). */
export function RunIssues({ run, ws }: { run: WebRunView; ws: string }) {
  const { t } = useI18n();
  const errorText = useRunErrorText();
  const [rows, setRows] = useState<RunIssueView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [action, setAction] = useState<Action>(null);

  const load = useCallback(async () => {
    try {
      setRows(await api.runIssues(run.id));
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [run.id]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="run-detail">
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? (error ? null : <Spinner />) : rows.length === 0 ? <p className="muted small">{t("issues.runIssuesNone")}</p> : (
        <ul className="plain">
          {rows.map((i) => (
            <li key={i.issueId}><Link to={`/w/${ws}/projects/${i.projectId}/issues/${i.issueId}`}>#{i.number} {i.title}</Link> <StatusBadge status={i.status} /></li>
          ))}
        </ul>
      )}
      {action === "link" ? (
        <RunIssueLinkForm ws={ws} runId={run.id} onCancel={() => setAction(null)} onLinked={() => {
          setAction(null);
          void load();
        }} />
      ) : action === "bug" ? <RunBugForm run={run} ws={ws} onCancel={() => setAction(null)} /> : (
        <div className="row-actions">
          <Button variant="ghost" onClick={() => setAction("link")}>{t("issues.runLinkTo")}</Button>
          {runFailed(run) ? <Button variant="ghost" onClick={() => setAction("bug")}>{t("issues.runCreateBug")}</Button> : null}
        </div>
      )}
    </div>
  );
}
