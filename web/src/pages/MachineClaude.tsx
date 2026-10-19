import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { AgentActionView, ClaudePermissionMode, MachineProjectView, MachineView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { versionAtLeast } from "../lib/version";
import { useCodeGate } from "./useCodeGate";

const MODES: readonly ClaudePermissionMode[] = ["manual", "plan", "acceptEdits"];
/** The environments Claude may be started in from the web (ADR-0014): the machine's class as an admin set it. */
const ALLOWED_ENVIRONMENTS = ["dev", "test"];

/** The sessions started from this page that nobody has stopped: a successful start that names a session, with no successful stop of that session after it. */
export function runningSessions(actions: AgentActionView[]): AgentActionView[] {
  const stopped = new Set(actions.filter((a) => a.kind === "claude_stop" && a.status === "succeeded" && a.sessionRef).map((a) => a.sessionRef));
  return actions.filter((a) => a.kind === "claude_start" && a.status === "succeeded" && a.sessionRef && !stopped.has(a.sessionRef));
}

/**
 * Starting and stopping Claude Code on this machine from the web (ADR-0014). Only the machine's owner sees it. The owner switches it on for the machine
 * (a recent sign-in, like asks), then picks one of the machine's own projects and a careful permission mode and starts a background session with
 * Claude Code's own `claude --bg`; each session started here can be stopped here. There is no free text anywhere: a project from the list, a mode from three.
 * Production and database machines never show a start. Claude Code must already trust the folder (that is done once, by hand, at the machine).
 */
export function MachineClaude({ agentId, view, owned, projects, agentVersion, version, onChanged }: {
  agentId: string; view: MachineView; owned: boolean; projects: MachineProjectView[]; agentVersion: string | null; version: number; onChanged: () => void;
}) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const enable = useCodeGate("reauth_required", "machines.claudeControl.enableHint");
  const action = useCodeGate("reauth_required", "machines.claudeControl.startHint");
  const [actions, setActions] = useState<AgentActionView[]>([]);
  const [error, setError] = useState<string | null>(null); // a stop that failed; a list that cannot be read is already said by the actions card above
  const [done, setDone] = useState<"saved" | "started" | "stopped" | null>(null);
  const [project, setProject] = useState("");
  const [mode, setMode] = useState<ClaudePermissionMode>("manual");
  const accepting = view.acceptsClaudeControl === true;
  const allowed = view.roleEnvironment != null && ALLOWED_ENVIRONMENTS.includes(view.roleEnvironment) && view.effectiveEnvironment != null && ALLOWED_ENVIRONMENTS.includes(view.effectiveEnvironment);
  const newEnough = versionAtLeast(agentVersion, config.claudeMinAgentVersion);
  const claudeActions = actions.filter((a) => a.kind === "claude_start" || a.kind === "claude_stop");
  const running = runningSessions(actions);
  const chosen = project || projects[0]?.key || "";

  const load = useCallback(async () => {
    try {
      setActions(await api.agentActions(agentId));
    } catch {
      // the actions card shows why the list cannot be read
    }
  }, [agentId]);

  useEffect(() => {
    if (owned) void load();
  }, [owned, version, load]);

  if (!owned) return <p className="muted small">{t("machines.claudeControl.ownerOnly")}</p>;

  const set = async (accept: boolean) => {
    setDone(null);
    if (await enable.attempt((code) => api.setAgentClaude(agentId, accept, code).then(() => undefined))) {
      setDone("saved");
      onChanged();
    }
  };
  const start = async () => {
    setDone(null);
    if (await action.attempt((code) => api.requestAgentAction(agentId, "claude_start", code, { projectKey: chosen, permissionMode: mode }).then(() => undefined))) {
      setDone("started");
      await load();
      onChanged();
    }
  };
  const stop = async (sessionRef: string) => {
    setDone(null);
    try {
      await api.requestAgentAction(agentId, "claude_stop", undefined, { sessionRef });
      setDone("stopped");
      await load();
      onChanged();
    } catch (e) {
      setError(errorText(e));
    }
  };
  const open = (kind: "claude_start" | "claude_stop") => actions.some((a) => a.kind === kind && (a.status === "queued" || a.status === "delivered"));

  return (
    <div className="stack">
      <p className="muted small">{t("machines.claudeControl.hint")}</p>
      {!newEnough ? <Notice>{t("machines.claudeControl.oldAgent", { min: config.claudeMinAgentVersion })}</Notice> : null}
      {!allowed ? <Notice>{t("machines.claudeControl.notAllowed")}</Notice> : null}
      <p>{accepting ? t("machines.claudeControl.on") : t("machines.claudeControl.off")}</p>
      {enable.field}
      {enable.error ? <Notice kind="error">{enable.error}</Notice> : null}
      <div className="row-actions">
        <Button variant={accepting ? "subtle" : "primary"} busy={enable.busy} onClick={() => void set(!accepting)}>
          {accepting ? t("machines.claudeControl.turnOff") : t("machines.claudeControl.turnOn")}
        </Button>
      </div>

      {accepting && allowed ? (
        <div className="stack">
          <h3>{t("machines.claudeControl.newTitle")}</h3>
          <p className="muted small">{t("machines.claudeControl.trustHint")}</p>
          {projects.length === 0 ? <p className="muted">{t("machines.claudeControl.noProjects")}</p> : (
            <div className="row">
              <label className="stack">
                <span className="small">{t("machines.claudeControl.project")}</span>
                <select value={chosen} onChange={(e) => setProject(e.target.value)}>
                  {projects.map((p) => <option key={p.key} value={p.key}>{p.name}</option>)}
                </select>
              </label>
              <label className="stack">
                <span className="small">{t("machines.claudeControl.mode")}</span>
                <select value={mode} onChange={(e) => setMode(e.target.value as ClaudePermissionMode)}>
                  {MODES.map((m) => <option key={m} value={m}>{t(`machines.claudeControl.modes.${m}`)}</option>)}
                </select>
              </label>
            </div>
          )}
          {action.field}
          {action.error ? <Notice kind="error">{action.error}</Notice> : null}
          <div className="row-actions">
            <Button variant="primary" busy={action.busy} disabled={!chosen || open("claude_start") || !action.ready || !newEnough || !view.online} onClick={() => void start()}>
              {t("machines.claudeControl.start")}
            </Button>
          </div>
        </div>
      ) : null}

      {done ? <Notice kind="success">{t(`machines.claudeControl.done.${done}`)}</Notice> : null}
      {error ? <Notice kind="error">{error}</Notice> : null}

      <h3>{t("machines.claudeControl.runningTitle")}</h3>
      {running.length === 0 ? <p className="muted">{t("machines.claudeControl.noneRunning")}</p> : (
        <ul className="stack">
          {running.map((a) => (
            <li key={a.id} className="row ask-row">
              <strong>{projects.find((p) => p.key === a.projectKey)?.name ?? a.projectKey}</strong>
              <code className="small">{a.sessionRef}</code>
              <span className="badge">{t(`machines.claudeControl.modes.${a.permissionMode ?? "manual"}`)}</span>
              <span className="muted small">{dateTime(a.createdAt)}</span>
              <Button variant="subtle" disabled={open("claude_stop")} onClick={() => void stop(a.sessionRef!)}>{t("machines.claudeControl.stop")}</Button>
            </li>
          ))}
        </ul>
      )}

      {claudeActions.length > 0 ? (
        <ul className="stack">
          {claudeActions.map((a) => (
            <li key={a.id} className="stack ask-row">
              <div className="row small">
                <strong>{t(`machines.actions.kind.${a.kind}`)}</strong>
                <span className={`badge status-${a.status}`}>{t(`machines.actions.status.${a.status}`)}</span>
                <span className="muted">{dateTime(a.createdAt)}</span>
              </div>
              {a.result ? <pre className="note-text action-result">{a.result}</pre> : null}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
