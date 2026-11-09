import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router";
import { api } from "../api/endpoints";
import { ApiError } from "../api/client";
import type { ExecLevelView, MachineView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n, type Key } from "../i18n";
import { dateTime } from "../lib/format";
import { approvalKeys } from "../lib/approvalKey";
import { EXEC_LEVELS, EXEC_LEVEL_LIFETIME_MS, execLevelPayload, raises, type ExecLevelName } from "../lib/execLevel";
import { useCodeGate } from "./useCodeGate";

const levelKey = (level: string): Key => `machines.execLevel.${level === "argv" || level === "shell" ? level : "off"}`;

const stateKey = (state: string): Key => {
  switch (state) {
    case "applied":
      return "machines.execLevel.state.applied";
    case "signature_refused":
      return "machines.execLevel.state.signature_refused";
    case "no_pinned_key":
      return "machines.execLevel.state.no_pinned_key";
    case "expired":
      return "machines.execLevel.state.expired";
    case "needs_admin":
      return "machines.execLevel.state.needs_admin";
    default:
      return "machines.execLevel.state.unknown";
  }
};

/**
 * The machine's remote-run level, changed from the web by its owner (ADR-0021). Raising it is signed in this browser with the approval key and the machine checks the
 * signature again against its pinned keys; lowering needs none. Every change is re-authenticated; a stale sequence (409) reloads the state. What the machine did with
 * the latest change is shown as a sentence, since only the machine's own report says the level really changed.
 */
export function MachineExecLevel({ agentId, view, owned, onChanged }: { agentId: string; view: MachineView; owned: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const gate = useCodeGate("reauth_required", "machines.execLevel.codeHint");
  const [state, setState] = useState<ExecLevelView | null>(null);
  const [choice, setChoice] = useState<ExecLevelName | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [noKey, setNoKey] = useState(false);
  const [done, setDone] = useState(false);

  const load = useCallback(async () => {
    try {
      setState(await api.agentExecLevel(agentId));
    } catch (e) {
      setProblem(errorText(e));
    }
    // errorText changes with the language only
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agentId]);
  useEffect(() => { void load(); }, [load, view.execLevel]);

  const current = state?.execLevel ?? view.execLevel;
  const selected = choice ?? (current as ExecLevelName);

  const save = async () => {
    if (!state) return;
    setDone(false);
    setNoKey(false);
    setProblem(null);
    const sequence = state.sequence + 1;
    const ok = await gate.attempt(async (code) => {
      const notAfter = new Date(Math.floor((Date.now() + EXEC_LEVEL_LIFETIME_MS) / 1000) * 1000).toISOString();
      let proof: { keyId: string; signature: string } | null = null;
      if (raises(current, selected)) {
        proof = await approvalKeys.sign(execLevelPayload(agentId, selected, sequence, notAfter));
        if (!proof) {
          setNoKey(true);
          return;
        }
      }

      try {
        setState(await api.setAgentExecLevel(agentId, { level: selected, sequence, notAfter, keyId: proof?.keyId, signature: proof?.signature, code }));
      } catch (e) {
        // A 409 means the sequence moved meanwhile: show what is stored now so the next save follows it.
        if (e instanceof ApiError && e.code === "exec_level_changed") await load();
        throw e;
      }

      setDone(true);
      setChoice(null);
    });
    if (ok) onChanged();
  };

  const report = state?.report ?? null;
  const reported = report && state && report.sequence >= state.sequence && state.sequence > 0;
  return (
    <div className="stack">
      <p className="muted small">{t("machines.execLevel.hint")}</p>
      <p>{t("machines.execLevel.current", { level: t(levelKey(current)) })}</p>
      {reported ? (
        <Notice kind={report.state === "applied" ? "success" : "info"}>
          {t(stateKey(report.state), { level: t(levelKey(state.execLevel)) })}{state.reportAt ? ` (${dateTime(state.reportAt)})` : ""}
        </Notice>
      ) : state && state.sequence > 0 ? <p className="muted small">{t("machines.execLevel.waiting", { sequence: state.sequence })}</p> : null}
      {problem ? <Notice kind="error">{problem}</Notice> : null}
      {owned ? (
        <div className="stack">
          <label className="field">
            <span>{t("machines.execLevel.label")}</span>
            <select className="input" value={selected} onChange={(e) => { setChoice(e.target.value as ExecLevelName); setDone(false); }}>
              {EXEC_LEVELS.map((l) => <option key={l} value={l}>{t(levelKey(l))}</option>)}
            </select>
          </label>
          {gate.field}
          {noKey ? (
            <Notice kind="error">{t("machines.execLevel.noKey")} <Link to="/account">{t("machines.execLevel.makeKey")}</Link></Notice>
          ) : null}
          {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
          {done ? <Notice kind="success">{t("machines.execLevel.saved")}</Notice> : null}
          <div className="row-actions">
            <Button busy={gate.busy} disabled={!state || selected === current || !gate.ready} onClick={() => void save()}>{t("machines.execLevel.save")}</Button>
          </div>
        </div>
      ) : <p className="muted small">{t("machines.execLevel.ownerOnly")}</p>}
    </div>
  );
}
