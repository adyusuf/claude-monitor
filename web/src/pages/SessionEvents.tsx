import { useEffect, useMemo, useState } from "react";
import { api } from "../api/endpoints";
import type { CommandRow } from "../api/types";
import { Button, Card } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { ActivityClass } from "../lib/activity";
import { isOpen, type OpenKeys, withOpen } from "../lib/fold";
import { ACTIVITY_FILTERS, ActivityFilter, type ActivityFilterName, buildTimeline, filterItems, groupRows, isFilter, type Row, type TimelineItem } from "../lib/timeline";
import { AssistantLine, GroupLine, InputLine, QuietLine, type Who } from "./ActivityItems";
import { useEventFeed } from "./useEventFeed";

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
  const { held: rows, next, older } = useEventFeed(sessionId, version, { pageSize: config.eventPageSize, refreshSize: config.eventPageSize });
  const [commands, setCommands] = useState<CommandRow[]>([]);
  const [filter, setFilter] = useState<ActivityFilterName>(storedFilter);
  const [opened, setOpened] = useState<OpenKeys>(new Set());

  useEffect(() => {
    api.commands(sessionId).then(setCommands).catch(() => setCommands([]));
  }, [sessionId, version]);

  const choose = (f: ActivityFilterName) => {
    setFilter(f);
    rememberFilter(f);
  };
  // Lines stay open as the reader left them whatever arrives: a line by its key, a group while any line in it is open (see lib/fold).
  const toggle = (key: string) => setOpened((o) => withOpen(o, [key], !o.has(key)));
  const groupKeys = (row: Extract<Row, { kind: "group" }>) => row.items.map((i) => `g:${i.key}`);
  const toggleGroup = (row: Extract<Row, { kind: "group" }>) => setOpened((o) => withOpen(o, groupKeys(row), !isOpen(o, groupKeys(row))));

  const timeline = useMemo(() => buildTimeline(rows, commands, next !== null), [rows, commands, next]);
  const visible = useMemo(() => filterItems(timeline.items, filter), [timeline, filter]);
  const pinned = useMemo(() => filterItems(timeline.pinned, filter), [timeline, filter]);
  const grouped = useMemo(() => groupRows(visible), [visible]);

  const line = (item: TimelineItem) => {
    const common = { item, open: opened.has(item.key), onToggle: toggle };
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
            <GroupLine key={row.key} row={row} open={isOpen(opened, groupKeys(row))} onToggle={toggleGroup}>
              {row.items.map(line)}
            </GroupLine>
          ))}
        </ol>
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void older()}>{t("session.olderEvents")}</Button></div> : null}
    </Card>
  );
}
