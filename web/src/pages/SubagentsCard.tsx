import { useMemo } from "react";
import type { SubagentRow } from "../api/types";
import { Card } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTimeSeconds, duration } from "../lib/format";
import { isRunning, orderSubagents, runtimeMs } from "../lib/subagents";
import { useNow } from "../lib/useNow";

const TICK_MS = 1000;

/** Every subagent of the session with when it started and ended and how long it ran (a running one counts up): the running ones on top, then the latest finished. */
export function SubagentsCard({ rows }: { rows: SubagentRow[] }) {
  const { t, dict } = useI18n();
  const running = rows.some(isRunning);
  const now = useNow(running ? TICK_MS : 60_000);
  const ordered = useMemo(() => orderSubagents(rows), [rows]);
  return (
    <Card title={t("session.subagents")}>
      {ordered.length === 0 ? <p className="muted">{t("session.noSubagents")}</p> : (
        <ul className="plain subagents">
          {ordered.map((r) => {
            const ran = runtimeMs(r, now);
            return (
              <li key={r.id} className={isRunning(r) ? "subagent subagent-running" : "subagent"}>
                <strong>{r.agentType}</strong> <span className="muted">{isRunning(r) ? `… ${t("session.subagentRunning")}` : "✓"}</span>
                {r.description ? <div className="muted small">{r.description}</div> : null}
                <dl className="subagent-times muted small">
                  <dt>{t("session.subagentStarted")}</dt><dd>{dateTimeSeconds(r.startedAt)}</dd>
                  {r.endedAt ? <><dt>{t("session.subagentEnded")}</dt><dd>{dateTimeSeconds(r.endedAt)}</dd></> : null}
                  {ran !== null ? <><dt>{t("session.subagentDuration")}</dt><dd>{duration(ran, dict.time)}</dd></> : null}
                </dl>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}
