import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { AgentActionKind, AgentActionView, MachineView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { versionAtLeast } from "../lib/version";
import { isStrict } from "./MachineBadges";
import { useCodeGate } from "./useCodeGate";

/** Restart and update change the machine: they ask for a recent sign-in (a fresh two-step code on a production or database machine). */
const GATED: readonly AgentActionKind[] = ["restart", "update"];
const KINDS: readonly AgentActionKind[] = ["status", "update_check", "update", "restart"];

/**
 * The fixed actions the machine's owner can ask of its agent (ADR-0005, "Agent actions from the web"): read its status, check for an update, update,
 * restart. There is no command text: each button is one kind from a closed set and the agent performs it with its own code. Only the owner sees the
 * buttons and the results; the result is text the agent wrote and is shown as text, never as markup.
 */
export function MachineActions({ agentId, view, owned, agentVersion, version, onChanged }: {
  agentId: string; view: MachineView; owned: boolean; agentVersion: string | null; version: number; onChanged: () => void;
}) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const strict = isStrict(view);
  const gate = useCodeGate("reauth_required", strict ? "machines.actions.strictHint" : "machines.actions.enableHint", strict);
  const [actions, setActions] = useState<AgentActionView[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState<AgentActionKind | null>(null);
  const newEnough = versionAtLeast(agentVersion, config.actionsMinAgentVersion);

  const load = useCallback(async () => {
    try {
      setActions(await api.agentActions(agentId));
      setError(null);
    } catch (e) {
      setError(errorText(e));
    }
    // errorText changes identity every render; the machine is what matters
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agentId]);

  useEffect(() => {
    if (owned) void load();
  }, [owned, version, load]);

  // The Claude actions have their own card (ADR-0013): they are not listed twice.
  const shown = actions.filter((a) => a.kind !== "claude_start" && a.kind !== "claude_stop");
  const open = (kind: AgentActionKind) => actions.some((a) => a.kind === kind && (a.status === "queued" || a.status === "delivered"));

  const ask = async (kind: AgentActionKind) => {
    setSent(null);
    const ok = await gate.attempt((code) => api.requestAgentAction(agentId, kind, GATED.includes(kind) ? code : undefined).then(() => undefined));
    if (ok) {
      setSent(kind);
      await load();
      onChanged();
    }
  };

  if (!owned) return <p className="muted small">{t("machines.actions.ownerOnly")}</p>;
  return (
    <div className="stack">
      <p className="muted small">{t("machines.actions.hint")}</p>
      {!newEnough ? <Notice>{t("machines.actions.oldAgent", { min: config.actionsMinAgentVersion })}</Notice> : null}
      {!view.online ? <p className="muted small">{t("machines.actions.offline")}</p> : null}
      {gate.field}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      {sent ? <Notice kind="success">{t("machines.actions.sent", { action: t(`machines.actions.kind.${sent}`) })}</Notice> : null}
      <div className="row-actions">
        {KINDS.map((kind) => (
          <Button key={kind} variant={GATED.includes(kind) ? "subtle" : "primary"} busy={gate.busy} disabled={open(kind) || (GATED.includes(kind) && !gate.ready)}
            onClick={() => void ask(kind)}>
            {t(`machines.actions.kind.${kind}`)}
          </Button>
        ))}
      </div>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {shown.length === 0 ? <p className="muted">{t("machines.actions.none")}</p> : (
        <ul className="stack">
          {shown.map((a) => (
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
      )}
    </div>
  );
}
