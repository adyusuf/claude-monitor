import { Icon } from "../components/icons";
import { useI18n } from "../i18n";
import type { MachineStats } from "../lib/sessionGroups";
import type { MachineView } from "../api/types";
import { OnlineDot } from "./MachineBits";

/**
 * A machine as a card on the sessions page: its name, how many sessions run on it now, how many wait for an answer and how many there are.
 * It is one button: pressing it opens or closes the machine's session list under it. `complete` is false while more sessions are still to be
 * loaded, so the count is then a floor ("3+").
 */
export function SessionMachineCard({ hostname, view, stats, complete, open, panelId, onToggle }: {
  hostname: string; view: MachineView | undefined; stats: MachineStats; complete: boolean; open: boolean; panelId: string; onToggle: () => void;
}) {
  const { t } = useI18n();
  return (
    <button type="button" className={open ? "machine-card open" : "machine-card"} aria-expanded={open} aria-controls={panelId} onClick={onToggle}>
      <span className="machine-card-name">{view ? <OnlineDot online={view.online} /> : null}<Icon name="machines" /> {hostname}</span>
      <span className="machine-card-count"><strong>{stats.active}{complete ? "" : "+"}</strong> {t("views.sessions.activeNow")}</span>
      <span className="muted small">
        {stats.waiting > 0 ? `${t("views.sessions.waiting", { n: stats.waiting })} · ` : ""}{t("views.sessions.total", { n: stats.total })}
      </span>
    </button>
  );
}
