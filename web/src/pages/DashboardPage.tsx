import { useEffect, useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { DashboardView, MachineView, StateCount } from "../api/types";
import { Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { debounce, isCatchUp, useLive } from "../lib/live";
import { maxDiskPct, memoryPct, OnlineDot } from "./MachineBits";
import { BarList, Meter, SoftwareMark } from "./DashboardBits";
import { statusKey } from "./RunList";

const APPROVAL_ORDER = ["allow", "auto_allowed", "deny", "expired", "open"] as const;
const countOf = (rows: StateCount[], state: string) => rows.find((r) => r.state === state)?.count ?? 0;

/** One machine: online or not, its resources as bars with their values, its open alerts and the software it reported. */
function MachineTile({ ws, m }: { ws: string; m: MachineView }) {
  const { t } = useI18n();
  const software = [...(m.interpreters ?? []), ...(m.tools ?? [])];
  const seen = new Set<string>();
  const marks = software.filter((s) => (seen.has(s.name) ? false : (seen.add(s.name), true)));
  return (
    <li className="dash-machine">
      <div className="dash-machine-head">
        <OnlineDot online={m.online} />
        <Link to={`/w/${ws}/machines/${m.agentId}`}>{m.hostname}</Link>
        <span className="badge">{m.os}</span>
        {m.openAlerts > 0 ? <span className="badge warn">{t("dashboard.alerts")}: {m.openAlerts}</span> : null}
      </div>
      <Meter label={t("dashboard.cpu")} pct={m.latest?.cpuPct ?? null} none={t("dashboard.notMeasured")} />
      <Meter label={t("dashboard.memory")} pct={memoryPct(m.latest)} none={t("dashboard.notMeasured")} />
      <Meter label={t("dashboard.disk")} pct={maxDiskPct(m.latest)} none={t("dashboard.notMeasured")} />
      <div className="dash-software" role="group" aria-label={t("dashboard.software")}>
        {marks.length === 0 ? <span className="muted small">{t("dashboard.noSoftware")}</span> : marks.map((s) => <SoftwareMark key={s.name} tool={s} />)}
      </div>
    </li>
  );
}

/**
 * The workspace dashboard: figures across the top (machines online, open alerts, projects, sprints active and passive), every machine with its
 * resources and software, runs by status and permission requests by outcome over the last 30 days. Re-read on every live change.
 */
export function DashboardPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const [view, setView] = useState<DashboardView | null>(null);
  const [machines, setMachines] = useState<MachineView[]>([]);
  const [error, setError] = useState<unknown>(null);
  // Each live change asks for one read after a short pause; a read the next one overtook is dropped, so older figures never win.
  const [tick, setTick] = useState(0);
  const reload = useMemo(() => debounce(() => setTick((n) => n + 1)), []);
  useEffect(() => {
    let current = true;
    Promise.all([api.dashboard(ws), api.machines(ws)]).then(([d, m]) => {
      if (!current) return;
      setView(d);
      setMachines(m);
      setError(null);
    }).catch((e: unknown) => {
      if (current) setError(e);
    });
    return () => {
      current = false;
    };
  }, [ws, tick]);
  const live = useLive(ws, (m) => {
    if (["machine", "run", "permission", "alert"].includes(m.event) || isCatchUp(m)) reload();
  });

  if (view === null) return <div className="page">{error ? <Notice kind="error">{errorText(error)}</Notice> : <Spinner />}</div>;
  const online = machines.filter((m) => m.online).length;
  const activeSprints = countOf(view.sprints, "active");
  const passiveSprints = countOf(view.sprints, "planned") + countOf(view.sprints, "closed");
  const approvals = APPROVAL_ORDER.map((k) => ({ key: k, label: t(`dashboard.approval.${k}`), count: countOf(view.approvals, k) }));
  const allowed = countOf(view.approvals, "allow") + countOf(view.approvals, "auto_allowed");
  const decided = allowed + countOf(view.approvals, "deny") + countOf(view.approvals, "expired");
  const runs = [...view.runs].sort((a, b) => b.count - a.count).map((r) => ({ key: r.state, label: t(statusKey(r.state)), count: r.count }));

  return (
    <div className="page">
      <header className="page-head">
        <h1>{t("dashboard.title")}</h1>
        <span className={live ? "live on" : "live"}><span className="dot" aria-hidden />{t("sessions.live")}</span>
      </header>
      <p className="muted">{t("dashboard.intro", { days: view.days })}</p>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      <div className="stat-tiles">
        <div className="stat-tile"><span className="stat-label">{t("dashboard.machines")}</span><span className="stat-value">{machines.length}</span>
          <span className="muted small">{t("dashboard.machinesOnline", { on: online, n: machines.length })}</span></div>
        <div className="stat-tile"><span className="stat-label">{t("dashboard.alerts")}</span><span className="stat-value">{view.openAlerts}</span></div>
        <div className="stat-tile"><span className="stat-label">{t("dashboard.projects")}</span><span className="stat-value">{countOf(view.projects, "active")}</span>
          <span className="muted small">{t("dashboard.projectsArchived", { n: countOf(view.projects, "archived") })}</span></div>
        <div className="stat-tile"><span className="stat-label">{t("dashboard.sprints")}</span>
          <span className="stat-value">{activeSprints} <span className="stat-sub">{t("dashboard.sprintsActive")}</span></span>
          <span className="muted small">{passiveSprints} {t("dashboard.sprintsPassive")} ({t("dashboard.sprintsPassiveHint")})</span></div>
      </div>
      <Card title={t("dashboard.machines")}>
        {machines.length === 0 ? <p className="muted">{t("dashboard.noMachines")}</p>
          : <ul className="dash-machines">{machines.map((m) => <MachineTile key={m.agentId} ws={ws} m={m} />)}</ul>}
      </Card>
      <div className="dash-charts">
        <Card title={t("dashboard.runs")}><BarList rows={runs} empty={t("dashboard.noRuns")} /></Card>
        <Card title={t("dashboard.approvals")} actions={decided > 0 ? <span className="muted small">{t("dashboard.approvedShare", { pct: Math.round((allowed / decided) * 100) })}</span> : null}>
          <BarList rows={approvals} empty={t("dashboard.noApprovals")} />
        </Card>
      </div>
    </div>
  );
}
