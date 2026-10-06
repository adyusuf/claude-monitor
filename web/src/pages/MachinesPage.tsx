import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import type { AgentRow, DeviceLookup } from "../api/types";
import { useSession } from "../auth/session";
import { Button, Card, Empty, Field, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";

/** The workspace's connected machines; a user disconnects their own, an admin anyone's. */
export function MachinesPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [rows, setRows] = useState<AgentRow[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const role = me?.workspaces.find((w) => w.id === ws)?.role;

  const load = useCallback(() => api.agents(ws).then(setRows).catch(setError), [ws]);
  useEffect(() => {
    void load();
  }, [load]);

  const revoke = async (id: string) => {
    if (!window.confirm(t("machines.revokeConfirm"))) return;
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
      {rows === null ? <Spinner /> : rows.length === 0 ? <Empty title={t("machines.empty")} hint={t("sessions.emptyHint")} /> : (
        <Card>
          <table className="table">
            <thead>
              <tr><th>{t("sessions.host")}</th><th>{t("machines.user")}</th><th>{t("machines.version")}</th><th>{t("machines.lastSeen")}</th><th /></tr>
            </thead>
            <tbody>
              {rows.map((a) => (
                <tr key={a.id} className={a.status === "revoked" ? "dim" : ""}>
                  <td><strong>{a.hostname}</strong> <span className="muted small">{a.os} {a.arch}</span></td>
                  <td>{a.userName}</td>
                  <td>
                    {a.version}
                    {a.status === "active" && a.updateAvailable
                      ? <> <span className="badge warn" title={t("machines.updateHint")}>{t("machines.updateAvailable", { version: a.latestVersion ?? "" })}</span></> : null}
                  </td>
                  <td>{a.status === "revoked" ? t("machines.revoked") : dateTime(a.lastHeartbeatAt ?? a.enrolledAt)}</td>
                  <td className="right">
                    {a.status === "active" && (a.userId === me?.id || role === "owner" || role === "admin")
                      ? <Button variant="ghost" onClick={() => void revoke(a.id)}>{t("machines.revoke")}</Button> : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  );
}

/** /device: the page "cm-agent login" opens; the signed-in person approves the code for a workspace. */
export function DevicePage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const navigate = useNavigate();
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
            <select className="input" value={workspace} onChange={(e) => e.target.value === "new" ? navigate("/workspaces/new") : setWorkspace(e.target.value)}>
              {(me?.workspaces ?? []).filter((w) => w.role !== "viewer").map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
            </select>
          </label>
          <div className="row-actions">
            <Button variant="ghost" disabled={!workspace} onClick={() => void decide(false)}>{t("device.deny")}</Button>
            <Button disabled={!workspace} onClick={() => void decide(true)}>{t("device.approve")}</Button>
          </div>
        </Card>
      )}
    </div>
  );
}
