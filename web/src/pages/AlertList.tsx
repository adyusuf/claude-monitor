import type { AlertView } from "../api/types";
import { VisibleText } from "../components/VisibleText";
import { useI18n } from "../i18n";
import { dateTime, percent } from "../lib/format";

/** A machine's alerts, newest first: what crossed which threshold, how high it went, and when it opened and closed. */
export function AlertList({ alerts }: { alerts: AlertView[] }) {
  const { t, tx } = useI18n();
  if (alerts.length === 0) return <p className="muted">{t("remote.noAlerts")}</p>;
  return (
    <ul className="plain alerts">
      {alerts.map((a) => (
        <li key={a.id} className={a.state === "open" ? "alert-row alert-open" : "alert-row"}>
          <span className={a.state === "open" ? "badge status-failed" : "badge ok"}>{tx(`remote.alertState.${a.state}`)}</span>
          <strong>{tx(`remote.alertKind.${a.kind}`)}</strong>
          {a.subject ? <code><VisibleText text={a.subject} /></code> : null}
          {a.kind === "offline" ? null : (
            <span className="muted small">
              {t("remote.alertNumbers", { last: percent(a.lastValue), peak: percent(a.peakValue), threshold: percent(a.thresholdPct) })}
            </span>
          )}
          <span className="muted small">{dateTime(a.openedAt)}{a.resolvedAt ? ` → ${dateTime(a.resolvedAt)}` : ""}</span>
        </li>
      ))}
    </ul>
  );
}
