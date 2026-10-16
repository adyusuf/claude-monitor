import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { AskView, MachineView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { useCodeGate } from "./useCodeGate";

/**
 * Asks from the user's other machines (ADR-0008 §3f). Only the machine's owner decides whether it takes any: turning it on needs a recent sign-in (the
 * gate of a grant), turning it off cancels the open ones. The owner also reads the asks sent here: the question and the answer are text another
 * Claude wrote, shown as plain text and never as markup.
 */
export function MachineAsks({ agentId, view, owned, onChanged }: { agentId: string; view: MachineView; owned: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const gate = useCodeGate("reauth_required", "machines.asks.enableHint");
  const [asks, setAsks] = useState<AskView[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const accepting = view.acceptsAsks === true;

  useEffect(() => {
    if (!owned) return;
    api.agentAsks(agentId).then(setAsks).catch((e: unknown) => setError(errorText(e)));
    // errorText changes identity every render; the machine and its state are what matter
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agentId, owned, accepting]);

  const set = async (accept: boolean) => {
    setDone(false);
    const ok = await gate.attempt((code) => api.setAgentAsks(agentId, accept, code).then(() => undefined));
    if (ok) {
      setDone(true);
      onChanged();
    }
  };

  return (
    <div className="stack">
      <p className="muted small">{t("machines.asks.hint")}</p>
      <p>{accepting ? t("machines.asks.on") : t("machines.asks.off")}</p>
      {owned ? (
        <div className="stack">
          {gate.field}
          {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
          {done ? <Notice kind="success">{t("machines.asks.saved")}</Notice> : null}
          <div className="row-actions">
            <Button variant={accepting ? "subtle" : "primary"} busy={gate.busy} onClick={() => void set(!accepting)}>
              {accepting ? t("machines.asks.turnOff") : t("machines.asks.turnOn")}
            </Button>
          </div>
        </div>
      ) : <p className="muted small">{t("machines.asks.ownerOnly")}</p>}
      {owned ? (
        <div className="stack">
          {error ? <Notice kind="error">{error}</Notice> : null}
          {asks.length === 0 ? <p className="muted">{t("machines.asks.none")}</p> : (
            <ul className="stack">
              {asks.map((a) => (
                <li key={a.id} className="stack ask-row">
                  <div className="row small"><span className="badge">{t(`machines.asks.status.${a.status}`)}</span><span className="muted">{dateTime(a.createdAt)}</span></div>
                  {a.question ? <p className="note-text"><strong>{t("machines.asks.question")}:</strong> {a.question}</p> : null}
                  {a.answer ? <p className="note-text"><strong>{t("machines.asks.answer")}:</strong> {a.answer}{a.answerTruncated ? <span className="muted"> ({t("machines.asks.answerCut")})</span> : null}</p> : null}
                </li>
              ))}
            </ul>
          )}
        </div>
      ) : null}
    </div>
  );
}
