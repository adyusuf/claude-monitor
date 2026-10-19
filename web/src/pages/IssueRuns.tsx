import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { IssueRunView } from "../api/typesIssues";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { IssueRunAttach } from "./IssueRunAttach";
import { RunTests, useRunErrorText, useRunKindText } from "./IssueRunBits";
import { RunStatusChip } from "./RunList";

function RunRow({ run, canWrite, onRemove }: { run: IssueRunView; canWrite: boolean; onRemove: () => void }) {
  const { t } = useI18n();
  const kindText = useRunKindText();
  return (
    <li className="spread">
      <span>
        {run.kind ? <><span className="badge">{kindText(run.kind)}</span>{" "}</> : null}
        <RunStatusChip status={run.status} />{" "}
        {run.exitCode !== null ? <><span className="muted small">{t("remote.exitCode", { code: run.exitCode })}</span>{" "}</> : null}
        <RunTests passed={run.testsPassed} failed={run.testsFailed} skipped={run.testsSkipped} />{" "}
        <strong>{run.targetHostname}</strong>{" "}
        <span className="muted small">{run.endedAt ? t("issues.runEnded", { when: dateTime(run.endedAt) }) : t("issues.runNotEnded")}</span>
      </span>
      {canWrite ? <Button variant="ghost" aria-label={`${t("issues.removeLink")}: ${run.targetHostname} ${dateTime(run.createdAt)}`} onClick={onRemove}>{t("issues.removeLink")}</Button> : null}
    </li>
  );
}

/** The remote runs attached to an issue (their outcome only, never the command or the output), with a picker to attach one of the workspace's recent runs. */
export function IssueRuns({ ws, issueId, canWrite }: { ws: string; issueId: string; canWrite: boolean }) {
  const { t } = useI18n();
  const errorText = useRunErrorText();
  const [rows, setRows] = useState<IssueRunView[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRows(await api.issueRuns(issueId));
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [issueId]);

  useEffect(() => {
    void load();
  }, [load]);

  const remove = async (runId: string) => {
    try {
      setError(null);
      await api.unlinkIssueRun(issueId, runId);
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <Card title={t("issues.runs")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <p className="muted">{t("issues.noRuns")}</p> : (
        <ul className="plain">
          {rows.map((r) => <RunRow key={r.runId} run={r} canWrite={canWrite} onRemove={() => void remove(r.runId)} />)}
        </ul>
      )}
      {canWrite && rows !== null ? (
        <IssueRunAttach ws={ws} issueId={issueId} linked={new Set(rows.map((r) => r.runId))} onAttached={load} onError={(e) => setError(errorText(e))} />
      ) : null}
    </Card>
  );
}
