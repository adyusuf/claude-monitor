import { useState } from "react";
import { api } from "../api/endpoints";
import type { MachineDetail } from "../api/types";
import { Button, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { useCodeGate } from "./useCodeGate";

/**
 * The listening services of a machine (ADR-0008 §3a′). Only the machine's owner decides whether the agent reports them; switching it on
 * needs a recent sign-in (the same gate as a grant), switching it off drops what was stored. The list is shown to the owner and to admins.
 */
export function MachineServices({ agentId, detail, owned, onChanged }: { agentId: string; detail: MachineDetail; owned: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const gate = useCodeGate("reauth_required", "machines.services.enableHint");
  const [done, setDone] = useState(false);
  const shared = detail.servicesShared;

  const set = async (share: boolean) => {
    setDone(false);
    const ok = await gate.attempt((code) => api.setAgentServices(agentId, share, code).then(() => undefined));
    if (ok) {
      setDone(true);
      onChanged();
    }
  };

  return (
    <div className="stack">
      <p className="muted small">{t("machines.services.hint")}</p>
      {shared && detail.services ? (
        detail.services.length > 0 ? (
          <>
            <ul className="chips" aria-label={t("machines.services.title")}>
              {detail.services.map((s) => <li key={s.port} className="badge">{s.port}{s.process ? ` ${s.process}` : ""}</li>)}
            </ul>
            {detail.servicesAt ? <p className="muted small">{t("machines.services.at", { at: dateTime(detail.servicesAt) })}</p> : null}
          </>
        ) : <p className="muted">{t("machines.services.waiting")}</p>
      ) : shared ? <p className="muted">{t("machines.services.hidden")}</p>
        : <p className="muted">{t("machines.services.off")}</p>}
      {owned ? (
        <div className="stack">
          {gate.field}
          {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
          {done ? <Notice kind="success">{t("machines.services.saved")}</Notice> : null}
          <div className="row-actions">
            <Button variant={shared ? "subtle" : "primary"} busy={gate.busy} onClick={() => void set(!shared)}>
              {shared ? t("machines.services.turnOff") : t("machines.services.turnOn")}
            </Button>
          </div>
        </div>
      ) : <p className="muted small">{t("machines.services.ownerOnly")}</p>}
    </div>
  );
}
