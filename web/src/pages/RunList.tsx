import { useState } from "react";
import { api } from "../api/endpoints";
import type { RunStatus, WebRunView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { useErrorText, useI18n, type Key } from "../i18n";
import { dateTime } from "../lib/format";
import { RunFacts } from "./RunFacts";
import { RunOutput } from "./RunOutput";

function statusKey(status: RunStatus | string): Key {
  switch (status) {
    case "pending_approval":
      return "remote.status.pending_approval";
    case "approved":
      return "remote.status.approved";
    case "delivered":
      return "remote.status.delivered";
    case "running":
      return "remote.status.running";
    case "succeeded":
      return "remote.status.succeeded";
    case "failed":
      return "remote.status.failed";
    case "timed_out":
      return "remote.status.timed_out";
    case "denied":
      return "remote.status.denied";
    case "expired":
      return "remote.status.expired";
    case "cancelled":
      return "remote.status.cancelled";
    default:
      return "remote.status.unknown";
  }
}

const tone = (status: string): string => {
  switch (status) {
    case "succeeded":
      return "ok";
    case "failed":
    case "timed_out":
    case "denied":
      return "bad";
    case "pending_approval":
    case "approved":
    case "delivered":
    case "running":
      return "open";
    default:
      return "quiet";
  }
};

export function RunStatusChip({ status }: { status: RunStatus | string }) {
  const { t } = useI18n();
  return <span className={`badge run-status run-status-${tone(status)}`}>{t(statusKey(status))}</span>;
}

/** A run still open on the target (or about to be): the requester or the owner may cancel it. */
const CANCELLABLE: readonly string[] = ["approved", "delivered", "running"];

/** The one-line summary of a run's command; the whole of it is in the expanded row. */
function Preview({ run }: { run: WebRunView }) {
  const { t } = useI18n();
  if (!run.visible) return <span className="muted small">{t("remote.commandHidden")}</span>;
  return <code className="run-preview"><VisibleText text={run.mode === "shell" ? (run.shellCommand ?? "") : (run.argv ?? []).join(" ")} /></code>;
}

function RunRow({ run, ws, version, onChanged }: { run: WebRunView; ws: string; version: number; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const cancel = async () => {
    try {
      await api.cancelRun(run.id);
      onChanged();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <li className="run-row">
      <div className="run-line">
        <RunStatusChip status={run.status} />
        <button type="button" className="run-toggle" aria-expanded={open} onClick={() => setOpen(!open)}>
          <Preview run={run} />
        </button>
        <span className="muted small">{run.requesterUser} · {run.requesterHostname}</span>
        <span className="muted small">{dateTime(run.createdAt)}</span>
        {run.visible && CANCELLABLE.includes(run.status) ? <Button variant="ghost" onClick={() => void cancel()}>{t("remote.cancelRun")}</Button> : null}
      </div>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {open ? (
        <div className="run-detail">
          <RunFacts run={run} ws={ws} />
          {run.exitCode !== null ? <p className="small">{t("remote.exitCode", { code: run.exitCode })}</p> : null}
          {run.error ? <p className="small">{t("remote.runError")}: {run.error}</p> : null}
          {run.visible && run.outputBytes > 0 ? <RunOutput runId={run.id} truncated={run.outputTruncated} version={version} /> : null}
        </div>
      ) : null}
    </li>
  );
}

/** A machine's run history, newest first; each row opens to the whole command, its facts and its output. */
export function RunList({ runs, ws, version, hasMore, onMore, onChanged }: {
  runs: WebRunView[]; ws: string; version: number; hasMore: boolean; onMore: () => void; onChanged: () => void;
}) {
  const { t } = useI18n();
  if (runs.length === 0) return <p className="muted">{t("remote.noRuns")}</p>;
  return (
    <>
      <ul className="plain run-list">
        {runs.map((r) => <RunRow key={r.id} run={r} ws={ws} version={version} onChanged={onChanged} />)}
      </ul>
      {hasMore ? <div className="more"><Button variant="ghost" onClick={onMore}>{t("sessions.more")}</Button></div> : null}
    </>
  );
}
