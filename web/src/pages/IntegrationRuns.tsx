import type { SyncRunView } from "../api/typesIntegrations";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";

/** The last checks of a connection: what ran, how it ended, and the numbers (never any text from the tool). */
export function IntegrationRuns({ runs }: { runs: SyncRunView[] }) {
  const { t, tx } = useI18n();
  if (runs.length === 0) return <p className="muted">{t("integrations.noRuns")}</p>;
  return (
    <ul className="plain">
      {runs.map((r) => (
        <li key={r.id}>
          <span className={r.status === "ok" ? "badge ok" : r.status === "running" ? "badge" : "badge warn"}>{t(`integrations.runStatus.${r.status}`)}</span>{" "}
          {t(`integrations.runKind.${r.kind}`)} · {dateTime(r.startedAt)} ·{" "}
          {t("integrations.runCounts", { created: r.created, updated: r.updated, archived: r.archived, people: r.people })}
          {r.error ? <> · <span className="muted">{tx(`errors.${r.error}`)}</span></> : null}
        </li>
      ))}
    </ul>
  );
}
