import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { WebRunView } from "../api/types";
import { Button, Spinner } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { attachableRuns } from "../lib/issueRuns";
import { issueLimits } from "../lib/issueOptions";
import { useRunKindText } from "./IssueRunBits";
import { statusKey } from "./RunList";

/** "Attach a run": opens a picker of the workspace's recent finished runs that are not on the issue yet (test runs first, then the newest). It shows host, kind, state and date, never the command. */
export function IssueRunAttach({ ws, issueId, linked, onAttached, onError }: {
  ws: string; issueId: string; linked: ReadonlySet<string>; onAttached: () => Promise<void>; onError: (e: unknown) => void;
}) {
  const { t } = useI18n();
  const kindText = useRunKindText();
  const [open, setOpen] = useState(false);
  const [runs, setRuns] = useState<WebRunView[] | null>(null);
  const [pick, setPick] = useState("");

  useEffect(() => {
    if (!open) return;
    let live = true;
    api.runs(ws, { limit: issueLimits.runsLimit }).then(
      (list) => live && setRuns(list),
      (e: unknown) => {
        if (live) {
          setOpen(false);
          onError(e);
        }
      },
    );
    return () => {
      live = false;
    };
    // onError is a fresh function each render; reading the runs again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, ws]);

  const close = () => {
    setOpen(false);
    setRuns(null);
    setPick("");
  };

  const attach = async () => {
    try {
      await api.linkIssueRun(issueId, pick);
      close();
      await onAttached();
    } catch (e) {
      onError(e);
    }
  };

  if (!open) return <div className="add-row"><Button variant="ghost" onClick={() => setOpen(true)}>{t("issues.attachRun")}</Button></div>;
  if (runs === null) return <Spinner />;
  const choices = attachableRuns(runs, linked);
  return (
    <form className="row-input add-row" onSubmit={(e) => {
      e.preventDefault();
      if (pick) void attach();
    }}>
      {choices.length === 0 ? <p className="muted">{t("issues.noRunsToAttach")}</p> : (
        <select className="input" aria-label={t("issues.runToAttach")} value={pick} onChange={(e) => setPick(e.target.value)}>
          <option value="">–</option>
          {choices.map((r) => <option key={r.id} value={r.id}>{`${r.targetHostname} · ${kindText(r.kind ?? null)} · ${t(statusKey(r.status))} · ${dateTime(r.endedAt ?? r.createdAt)}`}</option>)}
        </select>
      )}
      <Button type="submit" disabled={!pick}>{t("issues.attach")}</Button>
      <Button variant="ghost" onClick={close}>{t("common.cancel")}</Button>
    </form>
  );
}
