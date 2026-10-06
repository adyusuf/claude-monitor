import { useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { useSession } from "../auth/session";
import { Card, Empty, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { debounce, useLive } from "../lib/live";
import { AlertList } from "./AlertList";
import { GrantList } from "./GrantList";
import { JobList } from "./JobList";
import { MachineMetrics } from "./MachineMetrics";
import { ExecBadge, OnlineDot, ServiceBadge } from "./MachineBits";
import { RunApproval } from "./RunApproval";
import { RunList } from "./RunList";
import { useMachineData } from "./useMachineData";

/** The live events that change what this page shows. */
const REMOTE_EVENTS: readonly string[] = ["run", "alert", "grant", "job"];

/** One machine: its load, its alerts, the runs waiting for the owner, the run history, grants and jobs (ADR-0004). */
export function MachinePage() {
  const { ws = "", agentId = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [showResolved, setShowResolved] = useState(false);
  const [version, setVersion] = useState(0);
  const data = useMachineData(ws, agentId, me?.id, showResolved);
  const { reload } = data;

  const refresh = useMemo(() => debounce(() => {
    void reload();
    setVersion((v) => v + 1);
  }), [reload]);
  useLive(ws, (m) => {
    if (REMOTE_EVENTS.includes(m.event)) refresh();
  });

  if (!data.loaded) return <Spinner />;
  if (!data.machine) {
    return (
      <div className="page">
        <Link className="back" to={`/w/${ws}/machines`}>← {t("machines.title")}</Link>
        {data.error ? <Notice kind="error">{errorText(data.error)}</Notice> : <Empty title={t("errors.not_found")} />}
      </div>
    );
  }
  const m = data.machine;
  const waiting = data.runs.filter((r) => r.canDecide && r.status === "pending_approval");

  return (
    <div className="page">
      <Link className="back" to={`/w/${ws}/machines`}>← {t("machines.title")}</Link>
      <header className="page-head session-head">
        <div>
          <h1><OnlineDot online={m.online} /> {m.hostname}</h1>
          <div className="session-meta muted">
            <span>{m.os}</span>
            <span>◍ {m.userName}</span>
            <span>{t("machines.lastSeen")}: {m.lastSeenAt ? dateTime(m.lastSeenAt) : "–"}</span>
          </div>
        </div>
        <div className="head-side"><ExecBadge level={m.execLevel} /><ServiceBadge serviceMode={m.serviceMode} /></div>
      </header>
      {data.error ? <Notice kind="error">{errorText(data.error)}</Notice> : null}
      {m.execLevel === "off" ? <Notice>{t("remote.execOffNote")}</Notice> : null}

      {waiting.length > 0 ? (
        <section className="stack" aria-label={t("remote.waiting")}>
          <h2>{t("remote.waiting")}</h2>
          {waiting.map((r) => <RunApproval key={r.id} run={r} ws={ws} onChanged={() => void reload()} />)}
        </section>
      ) : null}

      <Card title={t("remote.resources", { minutes: config.metricsMinutes })}><MachineMetrics samples={data.metrics} /></Card>
      <Card title={t("remote.alerts")} actions={(
        <label className="check small">
          <input type="checkbox" checked={showResolved} onChange={(e) => setShowResolved(e.target.checked)} />
          <span>{t("remote.showResolved")}</span>
        </label>
      )}>
        <AlertList alerts={data.alerts} />
      </Card>
      <Card title={t("remote.runs")}>
        <RunList runs={data.runs} ws={ws} version={version} hasMore={data.hasMoreRuns} onMore={() => void data.moreRuns()} onChanged={() => void reload()} />
      </Card>
      <Card title={t("remote.grants")}>
        <GrantList grants={data.grants} agentId={agentId} canCreate={data.owned} onChanged={() => void reload()} />
      </Card>
      <Card title={t("remote.jobs")}><JobList jobs={data.jobs} onChanged={() => void reload()} /></Card>
    </div>
  );
}
