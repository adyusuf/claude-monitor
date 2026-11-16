import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import { ApiError } from "../api/client";
import type { AgentRow, DeviceLookup, MachineView } from "../api/types";
import { useSession } from "../auth/session";
import { MoreRows } from "../components/MoreRows";
import { Button, Card, Empty, Field, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime, percent } from "../lib/format";
import { appendNew, readUpTo, type ListRead } from "../lib/listPages";
import { debounce, isCatchUp, useLive } from "../lib/live";
import { ClaudeBadge, RoleBadge, TagChips } from "./MachineBadges";
import { ExecBadge, maxDiskPct, memoryPct, OnlineDot, ServiceBadge } from "./MachineBits";

/** One machine: the agent's facts, and (when the API reports remote work) its exec level, load and open alerts. */
function MachineRow({ ws, agent: a, view: v, canRevoke, onRevoke }: {
  ws: string; agent: AgentRow; view: MachineView | undefined; canRevoke: boolean; onRevoke: (id: string) => void;
}) {
  const { t } = useI18n();
  return (
    <tr className={a.status === "revoked" ? "dim" : ""}>
      <td>
        {v ? <OnlineDot online={v.online} /> : null}{" "}
        {v ? <Link to={`/w/${ws}/machines/${a.id}`}><strong>{a.hostname}</strong></Link> : <strong>{a.hostname}</strong>}{" "}
        <span className="muted small">{a.os} {a.arch}</span>
      </td>
      <td data-label={t("machines.user")}>{a.userName}</td>
      <td data-label={t("machines.role.title")}>{v ? <><RoleBadge view={v} /> <ClaudeBadge view={v} /> <TagChips view={v} /></> : null}</td>
      <td data-label={t("remote.remote")}>{v ? <><ExecBadge level={v.execLevel} /> <ServiceBadge serviceMode={v.serviceMode} /></> : null}</td>
      <td className="num" data-label={t("remote.cpu")}>{v ? percent(v.latest?.cpuPct) : ""}</td>
      <td className="num" data-label={t("remote.memory")}>{v ? percent(memoryPct(v.latest)) : ""}</td>
      <td className="num" data-label={t("remote.disk")}>{v ? percent(maxDiskPct(v.latest)) : ""}</td>
      <td className="num" data-label={t("remote.alerts")}>{v ? <span className={v.openAlerts > 0 ? "badge status-failed" : "muted"}>{v.openAlerts}</span> : ""}</td>
      <td data-label={t("machines.version")}>
        {a.version}
        {a.status === "active" && a.updateAvailable
          ? <> <span className="badge warn" title={t("machines.updateHint")}>{t("machines.updateAvailable", { version: a.latestVersion ?? "" })}</span></> : null}
      </td>
      <td className="nowrap" data-label={t("machines.lastSeen")}>{a.status === "revoked" ? t("machines.revoked") : dateTime(a.lastHeartbeatAt ?? a.enrolledAt)}</td>
      <td className="right">
        {canRevoke ? <Button variant="ghost" onClick={() => onRevoke(a.id)}>{t("machines.revoke")}</Button> : null}
      </td>
    </tr>
  );
}

const NONE: ListRead<MachineView> = { items: [], truncated: false };

/** The workspace's connected machines; a user disconnects their own, an admin anyone's. */
export function MachinesPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [rows, setRows] = useState<AgentRow[] | null>(null);
  const [views, setViews] = useState<Map<string, MachineView>>(new Map());
  const [error, setError] = useState<unknown>(null);
  // Whether the API cut the agents or the machines (X-Truncated, F-024), and how many of each are shown, so a live refresh keeps them.
  const [agentsCut, setAgentsCut] = useState(false);
  const [machinesCut, setMachinesCut] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const shown = useRef({ agents: 0, machines: 0 });
  // A refresh and Load more can overlap: each read takes a number and only the newest one is shown, so an older answer never wins.
  const generation = useRef(0);
  const role = me?.workspaces.find((w) => w.id === ws)?.role;

  const show = (agents: ListRead<AgentRow>, machines: ListRead<MachineView>) => {
    shown.current = { agents: agents.items.length, machines: machines.items.length };
    setRows(agents.items);
    setViews(new Map(machines.items.map((m) => [m.agentId, m])));
    setAgentsCut(agents.truncated);
    setMachinesCut(machines.truncated);
  };

  // An API without remote work answers 404 to the machines list: the page then shows the plain list, as before.
  const load = useCallback(async () => {
    const mine = ++generation.current;
    try {
      const [agents, machines] = await Promise.all([
        readUpTo((skip) => api.agentList(ws, skip), shown.current.agents, (a) => a.id),
        readUpTo((skip) => api.machineList(ws, skip), shown.current.machines, (m) => m.agentId).catch((e: unknown) => {
          if (e instanceof ApiError && e.status === 404) return NONE;
          throw e;
        }),
      ]);
      if (mine !== generation.current) return;
      show(agents, machines);
      setError(null);
    } catch (e) {
      if (mine === generation.current) setError(e);
    }
  }, [ws]);
  useEffect(() => {
    shown.current = { agents: 0, machines: 0 }; // another workspace starts at its first page
    void load();
  }, [load]);
  // A live message reads the list again through an effect, so the read (which keeps the rows shown) never runs from a render.
  const [tick, setTick] = useState(0);
  const reload = useMemo(() => debounce(() => setTick((n) => n + 1)), []);
  useEffect(() => {
    if (tick > 0) void load();
    // only a new tick reads again; a new workspace is read by the effect above
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick]);
  useLive(ws, (m) => {
    if (m.event === "alert" || m.event === "run" || m.event === "session" || m.event === "machine" || isCatchUp(m)) reload();
  });

  /**
   * The agents and machines after those shown, each list only while the API says it cut it; a row is added once. The rows wanted are
   * raised first, so a refresh that starts meanwhile reads as far itself (and wins, being newer).
   */
  const loadMore = async () => {
    if (!rows) return;
    const machines = [...views.values()];
    const mine = ++generation.current;
    shown.current = { agents: rows.length + (agentsCut ? 1 : 0), machines: machines.length + (machinesCut ? 1 : 0) };
    setLoadingMore(true);
    try {
      const [agentPage, machinePage] = await Promise.all([
        agentsCut ? api.agentList(ws, rows.length) : { items: [], truncated: false },
        machinesCut ? api.machineList(ws, machines.length) : NONE,
      ]);
      if (mine !== generation.current) return;
      show({ items: appendNew(rows, agentPage.items, (a) => a.id), truncated: agentPage.truncated },
        { items: appendNew(machines, machinePage.items, (m) => m.agentId), truncated: machinePage.truncated });
    } catch (e) {
      if (mine !== generation.current) return;
      shown.current = { agents: rows.length, machines: machines.length }; // nothing was added
      setError(e);
    } finally {
      setLoadingMore(false);
    }
  };

  const revoke = async (id: string) => {
    if (!window.confirm(t("machines.revokeConfirm"))) return;
    setError(null);
    try {
      await api.revokeAgent(id);
      await load();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>{t("machines.title")}</h1>
        <Link className="btn btn-primary" to="/download">{t("machines.connect")}</Link>
      </header>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {rows === null ? (error ? null : <Spinner />) : rows.length === 0 ? <Empty title={t("machines.empty")} hint={t("sessions.emptyHint")} /> : (
        <Card>
          <div className="table-scroll">
            <table className="table table-rows">
              <thead>
                <tr>
                  <th>{t("sessions.host")}</th><th>{t("machines.user")}</th><th>{t("machines.role.title")}</th><th>{t("remote.remote")}</th><th className="num">{t("remote.cpu")}</th>
                  <th className="num">{t("remote.memory")}</th><th className="num">{t("remote.disk")}</th><th className="num">{t("remote.alerts")}</th><th>{t("machines.version")}</th>
                  <th>{t("machines.lastSeen")}</th><th />
                </tr>
              </thead>
              <tbody>
                {rows.map((a) => (
                  <MachineRow key={a.id} ws={ws} agent={a} view={views.get(a.id)}
                    canRevoke={a.status === "active" && (a.userId === me?.id || role === "owner" || role === "admin")}
                    onRevoke={(id) => void revoke(id)} />
                ))}
              </tbody>
            </table>
          </div>
          {agentsCut || machinesCut ? <MoreRows note={t("machines.truncated", { n: rows.length })} busy={loadingMore} onMore={() => void loadMore()} /> : null}
        </Card>
      )}
    </div>
  );
}

/** /device: the page "aipod login" opens; the signed-in person approves the code for a workspace. */
export function DevicePage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [params] = useSearchParams();
  const [code, setCode] = useState(params.get("code") ?? "");
  const [device, setDevice] = useState<DeviceLookup | null>(null);
  const [picked, setWorkspace] = useState("");
  // The first workspace the user may contribute to, until they pick one (me may still be loading at first render).
  const workspace = picked || (me?.workspaces.find((w) => w.role !== "viewer")?.id ?? "");
  const [state, setState] = useState<"idle" | "approved" | "denied">("idle");
  const [error, setError] = useState<unknown>(null);

  const lookup = useCallback(async (value: string) => {
    setError(null);
    try {
      setDevice(await api.lookupDevice(value));
    } catch (e) {
      setDevice(null);
      setError(e);
    }
  }, []);

  useEffect(() => {
    const initial = params.get("code");
    if (initial) void lookup(initial);
  }, [lookup, params]);

  const decide = async (approve: boolean) => {
    try {
      if (approve) await api.approveDevice(device!.userCode, workspace);
      else await api.denyDevice(device!.userCode, workspace);
      setState(approve ? "approved" : "denied");
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="page narrow">
      <h1>{t("device.title")}</h1>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {state === "approved" ? <Notice kind="success">{t("device.approved")}</Notice> : state === "denied" ? <Notice>{t("device.denied")}</Notice> : !device ? (
        <Card>
          <form className="stack" onSubmit={(e) => {
            e.preventDefault();
            void lookup(code);
          }}>
            <Field label={t("device.code")} value={code} autoFocus placeholder="ABCD-EFGH" className="input code-input" onChange={(e) => setCode(e.target.value.toUpperCase())} />
            <Button type="submit">{t("device.lookup")}</Button>
          </form>
        </Card>
      ) : (
        <Card>
          <p className="device-code">{device.userCode}</p>
          <p className="muted">{t("device.details", { host: device.hostname, os: device.os, arch: device.arch, version: device.agentVersion })}</p>
          <label className="field">
            <span className="field-label">{t("device.workspace")}</span>
            <select className="input" value={workspace} onChange={(e) => setWorkspace(e.target.value)}>
              {(me?.workspaces ?? []).filter((w) => w.role !== "viewer").map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
            </select>
          </label>
          {workspace ? null : <p className="muted">{t("device.noWorkspace")} <Link to="/workspaces/new">{t("device.createWorkspace")}</Link></p>}
          <div className="row-actions">
            <Button variant="ghost" disabled={!workspace} onClick={() => void decide(false)}>{t("device.deny")}</Button>
            <Button disabled={!workspace} onClick={() => void decide(true)}>{t("device.approve")}</Button>
          </div>
        </Card>
      )}
    </div>
  );
}
