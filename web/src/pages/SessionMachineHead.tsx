import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import type { MachineDetail, MachineView } from "../api/types";
import { Icon } from "../components/icons";
import { Modal } from "../components/Modal";
import { Button, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { MachineClaude } from "./MachineClaude";
import { OnlineDot } from "./MachineBits";

/**
 * Whether Claude Code is on the machine and running there, as the agent last reported it: not installed, installed and not running, or running
 * with how many sessions. Nothing when the machine is not known (a disconnected one that still has sessions).
 */
export function ClaudeState({ view }: { view: MachineView }) {
  const { t } = useI18n();
  if (view.claudeKind === undefined || view.claudeKind === null) return <span className="badge">{t("sessions.claude.unknown")}</span>;
  if (view.claudeKind === "none") return <span className="badge">{t("sessions.claude.none")}</span>;
  const version = view.claudeVersion ?? "";
  const live = view.claudeSessionsLive;
  if (live === undefined || live === null) return <span className="badge">{t("sessions.claude.installed", { version })}</span>;
  return live > 0
    ? <span className="badge ok">{t("sessions.claude.running", { version, n: live })}</span>
    : <span className="badge">{t("sessions.claude.stopped", { version })}</span>;
}

/** The machine's own Claude controls (ADR-0014) in a dialog: switch it on, start a session in one of its projects, stop one started from the web. */
function ClaudeDialog({ view, agentVersion, onClose, onChanged }: { view: MachineView; agentVersion: string | null; onClose: () => void; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [detail, setDetail] = useState<MachineDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);
  const load = useCallback(async () => {
    try {
      setDetail(await api.machineDetail(view.agentId));
      setVersion((v) => v + 1);
      setError(null);
    } catch (e) {
      setError(e);
    }
  }, [view.agentId]);
  useEffect(() => {
    void load();
  }, [load]);
  return (
    <Modal title={t("sessions.claude.dialog", { host: view.hostname })} onClose={onClose}>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {detail === null ? (error ? null : <Spinner />) : (
        <MachineClaude agentId={view.agentId} view={detail.machine} owned projects={detail.projects} agentVersion={agentVersion} version={version}
          onChanged={() => {
            void load();
            onChanged();
          }} />
      )}
    </Modal>
  );
}

/**
 * The head of a machine's card on the sessions page: online or not, whose it is, whether Claude is installed and running, and for its owner the
 * button that starts or stops Claude there. A machine the page does not know (disconnected) shows only its name.
 */
export function SessionMachineHead({ ws, hostname, view, owned, agentVersion, count, onChanged }: {
  ws: string; hostname: string; view: MachineView | undefined; owned: boolean; agentVersion: string | null; count: number; onChanged: () => void;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  return (
    <header className="session-machine-head">
      <h2 className="session-machine-name">
        {view ? <OnlineDot online={view.online} /> : null}
        <Icon name="machines" /> {view ? <Link to={`/w/${ws}/machines/${view.agentId}`}>{hostname}</Link> : hostname}
        <span className="badge" title={t("sessions.count", { n: count })}>{count}</span>
      </h2>
      {view ? (
        <div className="session-machine-facts">
          <span className="muted small"><Icon name="members" /> {view.userName}</span>
          <ClaudeState view={view} />
          {view.acceptsClaudeControl ? <span className="badge">{t("sessions.claude.fromWebOn")}</span> : null}
          {owned && view.claudeKind && view.claudeKind !== "none" ? <Button variant="ghost" onClick={() => setOpen(true)}>{t("sessions.claude.control")}</Button> : null}
        </div>
      ) : <span className="muted small">{t("sessions.machineGone")}</span>}
      {open && view ? <ClaudeDialog view={view} agentVersion={agentVersion} onClose={() => setOpen(false)} onChanged={onChanged} /> : null}
    </header>
  );
}
