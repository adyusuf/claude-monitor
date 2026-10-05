import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../api/endpoints";
import type { CommandRow, EventRow } from "../api/types";
import { Button, Card } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { ActivityClass } from "../lib/activity";
import { ACTIVITY_FILTERS, ActivityFilter, type ActivityFilterName, buildTimeline, filterItems, groupRows, isFilter, type TimelineItem } from "../lib/timeline";
import { AssistantLine, GroupLine, InputLine, QuietLine, type Who } from "./ActivityItems";

function storedFilter(): ActivityFilterName {
  try {
    const saved = localStorage.getItem(config.activityFilterKey);
    if (isFilter(saved)) return saved;
  } catch {
    // storage unavailable: show everything
  }
  return ActivityFilter.All;
}

function rememberFilter(filter: ActivityFilterName) {
  try {
    localStorage.setItem(config.activityFilterKey, filter);
  } catch {
    // storage unavailable: the choice lasts for this page only
  }
}

/**
 * The session's activity, newest first, an older page at a time. What enters the session (a command sent from the web,
 * a prompt typed in the harness) stands out; Claude's answers are plain; tool calls and background events are quiet.
 */
export function EventsCard({ sessionId, owner, version }: { sessionId: string; owner: Who; version: number }) {
  const { t } = useI18n();
  const [rows, setRows] = useState<EventRow[]>([]);
  const [commands, setCommands] = useState<CommandRow[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [filter, setFilter] = useState<ActivityFilterName>(storedFilter);
  const [open, setOpen] = useState<string | null>(null);
  const [openGroups, setOpenGroups] = useState<ReadonlySet<string>>(new Set());

  const load = useCallback(async () => {
    const page = await api.events(sessionId, { limit: config.eventPageSize });
    setRows(page.items);
    setNext(page.next);
  }, [sessionId]);

  useEffect(() => {
    load().catch(() => setRows([]));
    api.commands(sessionId).then(setCommands).catch(() => setCommands([]));
  }, [load, sessionId, version]);

  const older = async () => {
    const page = await api.events(sessionId, { before: next, limit: config.eventPageSize });
    setRows((r) => [...r, ...page.items]);
    setNext(page.next);
  };

  const choose = (f: ActivityFilterName) => {
    setFilter(f);
    rememberFilter(f);
  };
  const toggle = (key: string) => setOpen((k) => (k === key ? null : key));
  const toggleGroup = (key: string) => setOpenGroups((g) => {
    const copy = new Set(g);
    if (!copy.delete(key)) copy.add(key);
    return copy;
  });

  const timeline = useMemo(() => buildTimeline(rows, commands, next !== null), [rows, commands, next]);
  const visible = useMemo(() => filterItems(timeline.items, filter), [timeline, filter]);
  const pinned = useMemo(() => filterItems(timeline.pinned, filter), [timeline, filter]);
  const grouped = useMemo(() => groupRows(visible), [visible]);

  const line = (item: TimelineItem) => {
    const common = { item, open: open === item.key, onToggle: toggle };
    switch (item.cls) {
      case ActivityClass.MonitorInput:
      case ActivityClass.HumanInput:
        return <InputLine key={item.key} who={owner} {...common} />;
      case ActivityClass.Assistant:
        return <AssistantLine key={item.key} {...common} />;
      default:
        return <QuietLine key={item.key} {...common} />;
    }
  };

  const empty = timeline.items.length === 0 && timeline.pinned.length === 0;
  return (
    <Card title={t("session.events")}>
      <div className="segmented" role="group" aria-label={t("activity.filterLabel")}>
        {ACTIVITY_FILTERS.map((f) => (
          <button key={f} type="button" className={`seg${filter === f ? " active" : ""}`} aria-pressed={filter === f} onClick={() => choose(f)}>
            {t(`activity.${f}`)}
          </button>
        ))}
      </div>
      {pinned.length > 0 ? (
        <>
          <h3 className="sub">{t("activity.pendingTitle")}</h3>
          <ol className="act-list">{pinned.map(line)}</ol>
        </>
      ) : null}
      {empty ? <p className="muted">{t("session.noEvents")}</p> : grouped.length === 0 && pinned.length === 0 ? <p className="muted">{t("activity.emptyFilter")}</p> : (
        <ol className="act-list">
          {grouped.map((row) => row.kind === "item" ? line(row.item) : (
            <GroupLine key={row.key} row={row} open={openGroups.has(row.key)} onToggle={toggleGroup}>
              {row.items.map(line)}
            </GroupLine>
          ))}
        </ol>
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void older()}>{t("session.olderEvents")}</Button></div> : null}
    </Card>
  );
}
