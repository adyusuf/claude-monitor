import { useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { useSession } from "../auth/session";
import { Tabs } from "../components/Tabs";
import { Card, Empty, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { debounce, isCatchUp, useLive } from "../lib/live";
import { AlertList } from "./AlertList";
import { GrantList } from "./GrantList";
import { JobList } from "./JobList";
import { ScheduleList } from "./ScheduleList";
import { ToolList } from "./MachineBadges";
import { MachineMetrics } from "./MachineMetrics";
import { NoteEditor } from "./MachineFacts";
import { TagsEditor } from "./MachineTagsEditor";
import { MachineActions } from "./MachineActions";
import { MachineClaude } from "./MachineClaude";
import { MachineInventory } from "./MachineInventory";
import { MachineAccess } from "./MachineAccess";
import { MachineAsks } from "./MachineAsks";
import { MachineExecLevel } from "./MachineExecLevel";
import { MachineProjects } from "./MachineProjects";
import { MachineRoleEditor } from "./MachineRoleEditor";
import { MachineServices } from "./MachineServices";
import { ExecBadge, OnlineDot, ServiceBadge } from "./MachineBits";
import { RunApproval } from "./RunApproval";
import { RunList } from "./RunList";
import { useMachineData } from "./useMachineData";
import { Icon } from "../components/icons";

/** The live events that change what this page shows. */
const REMOTE_EVENTS: readonly string[] = ["run", "alert", "grant", "job", "machine", "action"];

/** One machine in tabs: overview, control, work (projects, runs, jobs, schedules), monitoring (load, alerts) and access (grants). The runs waiting for the owner stay above the tabs. */
export function MachinePage() {
  const { ws = "", agentId = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [showResolved, setShowResolved] = useState(false);
  const [version, setVersion] = useState(0);
  const data = useMachineData(ws, agentId, me?.id, showResolved);
  const role = me?.workspaces.find((w) => w.id === ws)?.role;
  const isAdmin = role === "owner" || role === "admin";
  const { reload } = data;

  const refresh = useMemo(() => debounce(() => {
    void reload();
    setVersion((v) => v + 1);
  }), [reload]);
  useLive(ws, (m) => {
    if (REMOTE_EVENTS.includes(m.event) || isCatchUp(m)) refresh();
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
    <div className="page machine-page">
      <Link className="back" to={`/w/${ws}/machines`}>← {t("machines.title")}</Link>
      <header className="page-head session-head">
        <div>
          <h1><OnlineDot online={m.online} /> {m.hostname}</h1>
          <div className="session-meta muted">
            <span>{m.os}</span>
            <span><Icon name="members" /> {m.userName}</span>
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
      <Tabs label={t("views.machine.tabs")} tabs={[
        {
          id: "overview", label: t("views.machine.overview"),
          content: (
            <>
              <Card title={t("machines.role.title")}><MachineRoleEditor agentId={agentId} view={m} canEdit={isAdmin} onChanged={() => void reload()} /></Card>
              <Card title={t("machines.tags.title")}><TagsEditor agentId={agentId} tags={m.tags} canEdit={isAdmin} onChanged={() => void reload()} /></Card>
              <Card title={t("machines.note.title")}><NoteEditor agentId={agentId} note={data.detail?.note} canEdit={isAdmin} onChanged={() => void reload()} /></Card>
              <Card title={t("machines.tools.title")}><ToolList view={m} /></Card>
            </>
          ),
        },
        {
          id: "control", label: t("views.machine.control"), hidden: !data.detail,
          content: data.detail ? (
            <>
              <Card title={t("machines.services.title")}><MachineServices agentId={agentId} detail={data.detail} owned={data.owned} onChanged={() => void reload()} /></Card>
              <Card title={t("machines.execLevel.title")}><MachineExecLevel agentId={agentId} view={m} owned={data.owned} onChanged={() => void reload()} /></Card>
              <Card title={t("machines.actions.title")}>
                <MachineActions agentId={agentId} view={m} owned={data.owned} agentVersion={data.agentVersion} version={version} onChanged={() => void reload()} />
              </Card>
              <Card title={t("machines.claudeControl.title")}>
                <MachineClaude agentId={agentId} view={m} owned={data.owned} projects={data.detail.projects} agentVersion={data.agentVersion} version={version} onChanged={() => void reload()} />
              </Card>
              {data.owned || data.detail.inventory ? (
                <Card title={t("machines.inventory.title")}><MachineInventory agentId={agentId} detail={data.detail} owned={data.owned} agentVersion={data.agentVersion} onChanged={() => void reload()} /></Card>
              ) : null}
              <Card title={t("machines.asks.title")}><MachineAsks agentId={agentId} view={m} owned={data.owned} onChanged={() => void reload()} /></Card>
            </>
          ) : null,
        },
        {
          id: "work", label: t("views.machine.work"),
          content: (
            <>
              {data.detail ? <Card title={t("machines.projects.title")}><MachineProjects projects={data.detail.projects} agentId={agentId} owned={data.owned} onStarted={() => void reload()} /></Card> : null}
              <Card title={t("remote.runs")}>
                <RunList runs={data.runs} ws={ws} version={version} hasMore={data.hasMoreRuns} onMore={() => void data.moreRuns()} onChanged={() => void reload()} />
              </Card>
              <Card title={t("remote.jobs")}><JobList jobs={data.jobs} onChanged={() => void reload()} /></Card>
              <Card title={t("schedules.title")}><ScheduleList schedules={data.schedules} jobs={data.jobs} canCreate={data.owned} onChanged={() => void reload()} /></Card>
            </>
          ),
        },
        {
          id: "monitoring", label: t("views.machine.monitoring"),
          content: (
            <>
              <Card title={t("remote.resources", { minutes: config.metricsMinutes })}><MachineMetrics samples={data.metrics} /></Card>
              <Card title={t("remote.alerts")} actions={(
                <label className="check small">
                  <input type="checkbox" checked={showResolved} onChange={(e) => setShowResolved(e.target.checked)} />
                  <span>{t("remote.showResolved")}</span>
                </label>
              )}>
                <AlertList alerts={data.alerts} />
              </Card>
            </>
          ),
        },
        {
          id: "access", label: t("views.machine.access"),
          content: (
            <>
              {data.detail?.access ? <Card title={t("access.machineTitle")}><MachineAccess ws={ws} access={data.detail.access} /></Card> : null}
              <Card title={t("remote.grants")}>
                <GrantList grants={data.grants} agentId={agentId} canCreate={data.owned} onChanged={() => void reload()} />
              </Card>
            </>
          ),
        },
      ]} />
    </div>
  );
}
