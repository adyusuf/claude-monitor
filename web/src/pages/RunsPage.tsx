import { useCallback, useEffect, useRef, useState } from "react";
import { useParams } from "react-router";
import { api } from "../api/endpoints";
import type { AgentRow, WebRunView } from "../api/types";
import { Modal } from "../components/Modal";
import { Button, Card, Empty, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { isCatchUp, useLive } from "../lib/live";
import { RunApproval } from "./RunApproval";
import { cancellable, Preview, RunDetail, RunStatusChip } from "./RunList";

/**
 * Who decided a run: the grant or job that allowed it on its own (the API records the owner as the decider then, so it is checked first), else
 * the person who allowed or denied it; "not yet" only while it still waits, a dash for a run that ended undecided (expired, cancelled).
 */
function Decider({ run }: { run: WebRunView }) {
  const { t } = useI18n();
  if (run.grantId) return <span className="muted">{t("runsPage.byGrant")}</span>;
  if (run.jobId) return <span className="muted">{t("runsPage.byJob")}</span>;
  if (run.decidedByName) return <>{run.decidedByName}{run.decidedAt ? <span className="muted small"> · {dateTime(run.decidedAt)}</span> : null}</>;
  return <span className="muted">{run.status === "pending_approval" ? t("runsPage.undecided") : "—"}</span>;
}

/**
 * The first page read again, put in front of the older runs already loaded: those keep their place, so a live update never shrinks the list. A
 * first page shorter than a page is the whole list.
 */
export function mergeFirstPage(page: WebRunView[], loaded: WebRunView[], pageSize: number): WebRunView[] {
  const last = page[page.length - 1];
  if (!last || page.length < pageSize) return page;
  const ids = new Set(page.map((r) => r.id));
  return [...page, ...loaded.filter((r) => !ids.has(r.id) && r.createdAt < last.createdAt)];
}

/** One run in the popup: the approval card while it waits for this user, then its whole detail, and a cancel while it is open. */
function RunPopup({ run, ws, version, onClose, onChanged }: { run: WebRunView; ws: string; version: number; onClose: () => void; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
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
    <Modal title={<>{t("runsPage.detailsOf")} <RunStatusChip status={run.status} /></>} onClose={onClose}>
      {run.canDecide && run.status === "pending_approval" ? <RunApproval run={run} ws={ws} onChanged={onChanged} /> : (
        <>
          <dl className="facts">
            <dt>{t("runsPage.when")}</dt><dd>{dateTime(run.createdAt)}</dd>
            <dt>{t("runsPage.decidedBy")}</dt><dd><Decider run={run} /></dd>
          </dl>
          <RunDetail run={run} ws={ws} version={version} />
        </>
      )}
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {cancellable(run) ? <div className="row-actions"><Button variant="ghost" onClick={() => void cancel()}>{t("remote.cancelRun")}</Button></div> : null}
    </Modal>
  );
}

/** Every run of the workspace, newest first, optionally one machine's; a row opens the run in a popup. Kept current by the live stream. */
export function RunsPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [runs, setRuns] = useState<WebRunView[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [agents, setAgents] = useState<AgentRow[]>([]);
  const [agent, setAgent] = useState("");
  const [openId, setOpenId] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const [error, setError] = useState<unknown>(null);
  // A response is used only while the filter it was asked for is still the one shown, and only the latest read of the first page counts.
  const filter = `${ws}/${agent}`;
  const shown = useRef(filter);
  const reads = useRef(0);
  useEffect(() => {
    shown.current = filter;
  }, [filter]);

  /** Reads the first page; `fresh` replaces the list (a new filter), otherwise the page is merged into what is loaded. */
  const load = useCallback(async (fresh: boolean) => {
    const asked = `${ws}/${agent}`;
    const read = ++reads.current;
    try {
      const page = await api.runs(ws, { agent: agent || undefined, limit: config.runPageSize });
      if (shown.current !== asked || read !== reads.current) return;
      setRuns((r) => (fresh || !r ? page : mergeFirstPage(page, r, config.runPageSize)));
      if (fresh || page.length < config.runPageSize) setHasMore(page.length === config.runPageSize);
      setVersion((v) => v + 1);
      setError(null);
    } catch (e) {
      if (shown.current === asked) setError(e);
    }
  }, [ws, agent]);

  useEffect(() => {
    setRuns(null);
    void load(true);
  }, [load]);
  useEffect(() => {
    api.agents(ws).then(setAgents).catch(() => setAgents([])); // the filter is a convenience; the list works without it
  }, [ws]);
  const reloadTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(reloadTimer.current), []);

  // The run in the popup is read again on every update, so it stays current even when it is older than the first page.
  useEffect(() => {
    if (!openId) return;
    let live = true;
    api.run(openId).then((fresh) => {
      if (live) setRuns((r) => r?.map((x) => (x.id === fresh.id ? fresh : x)) ?? r);
    }).catch(() => undefined); // the row already shown stays; the popup is only a closer look
    return () => {
      live = false;
    };
  }, [openId, version]);
  const live = useLive(ws, (m) => {
    if (m.event !== "run" && !isCatchUp(m)) return;
    clearTimeout(reloadTimer.current);
    reloadTimer.current = setTimeout(() => void load(false), config.liveDebounceMs);
  });

  const more = async () => {
    const oldest = runs?.[runs.length - 1];
    if (!oldest) return;
    const asked = filter;
    try {
      const page = await api.runs(ws, { agent: agent || undefined, before: oldest.createdAt, limit: config.runPageSize });
      if (shown.current !== asked) return;
      setRuns((r) => [...(r ?? []), ...page.filter((p) => !(r ?? []).some((x) => x.id === p.id))]);
      setHasMore(page.length === config.runPageSize);
    } catch (e) {
      if (shown.current === asked) setError(e);
    }
  };

  const opened = runs?.find((r) => r.id === openId) ?? null;
  return (
    <div className="page">
      <header className="page-head">
        <h1>{t("runsPage.title")}</h1>
        <span className={live ? "live on" : "live"}><span className="dot" aria-hidden />{t("sessions.live")}</span>
      </header>
      <p className="muted">{t("runsPage.intro")}</p>
      <div className="toolbar">
        <select className="input" aria-label={t("runsPage.machine")} value={agent} onChange={(e) => setAgent(e.target.value)}>
          <option value="">{t("runsPage.allMachines")}</option>
          {agents.map((a) => <option key={a.id} value={a.id}>{a.hostname} · {a.userName}</option>)}
        </select>
      </div>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {runs === null ? <Spinner /> : runs.length === 0 ? <Empty title={t("runsPage.empty")} /> : (
        <Card>
          <div className="table-scroll">
            <table className="table table-rows runs-table">
              <thead>
                <tr>
                  <th>{t("runsPage.status")}</th><th>{t("runsPage.command")}</th><th>{t("runsPage.machine")}</th>
                  <th>{t("runsPage.requestedBy")}</th><th>{t("runsPage.when")}</th><th>{t("runsPage.decidedBy")}</th><th className="right" />
                </tr>
              </thead>
              <tbody>
                {runs.map((r) => (
                  <tr key={r.id}>
                    <td data-label={t("runsPage.status")}><RunStatusChip status={r.status} /></td>
                    <td data-label={t("runsPage.command")} className="runs-command"><Preview run={r} /></td>
                    <td data-label={t("runsPage.machine")}>{r.targetHostname} <span className="muted small">{r.targetUser}</span></td>
                    <td data-label={t("runsPage.requestedBy")}>{r.requesterUser} <span className="muted small">{r.requesterHostname}</span></td>
                    <td data-label={t("runsPage.when")} className="nowrap">{dateTime(r.createdAt)}</td>
                    <td data-label={t("runsPage.decidedBy")}><Decider run={r} /></td>
                    <td className="right"><Button variant="ghost" onClick={() => setOpenId(r.id)}>{t("runsPage.details")}</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {hasMore ? <div className="more"><Button variant="ghost" onClick={() => void more()}>{t("sessions.more")}</Button></div> : null}
        </Card>
      )}
      {opened ? <RunPopup run={opened} ws={ws} version={version} onClose={() => setOpenId(null)} onChanged={() => void load(false)} /> : null}
    </div>
  );
}
