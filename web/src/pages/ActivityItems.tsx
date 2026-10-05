import type { ReactNode } from "react";
import { Json } from "../components/ui";
import { useI18n } from "../i18n";
import { ActivityClass, CommandKind, kindLabel } from "../lib/activity";
import { time } from "../lib/format";
import type { Row, TimelineItem } from "../lib/timeline";

// How each kind of line looks. The two INPUT kinds are cards with a label, an icon and a colour (never the colour
// alone); Claude's answers are plain with a thin stripe; tool calls and background events are quiet single lines.

/** Who the lines are about: the session's owner types in Claude Code and is the only one who may send commands. */
export interface Who { ownerId: string; ownerName: string }

interface LineProps {
  item: TimelineItem;
  who: Who;
  open: boolean;
  onToggle: (key: string) => void;
}

/** Text presentation (U+FE0E), so the icons take the line's colour instead of drawing as emoji. */
const ICON = { monitor: "\u2601\uFE0E", human: "\u2328\uFE0E" } as const;

const moment = (item: TimelineItem) => (item.at ? time(new Date(item.at).toISOString()) : "");

/** The payload of an event, shown under its line while the line is open. */
function Details({ item, open }: { item: TimelineItem; open: boolean }) {
  return open && item.event ? <Json value={item.event.payload} /> : null;
}

function Truncated({ item }: { item: TimelineItem }) {
  const { t } = useI18n();
  return item.event?.truncated ? <span className="badge">{t("session.truncated")}</span> : null;
}

/** A line that opens its payload when pressed; a command has no payload and is plain text. */
function Pressable({ item, open, onToggle, className, children }: Pick<LineProps, "item" | "open" | "onToggle"> & { className: string; children: ReactNode }) {
  if (!item.event) return <div className={className}>{children}</div>;
  return (
    <button type="button" className={className} onClick={() => onToggle(item.key)} aria-expanded={open}>
      {children}
    </button>
  );
}

/** A message that entered the session: sent from the web (a command) or typed in the harness. */
export function InputLine({ item, who, open, onToggle }: LineProps) {
  const { t, dict } = useI18n();
  const monitor = item.cls === ActivityClass.MonitorInput;
  const command = item.command;
  const stop = command?.kind === CommandKind.Stop;
  const label = monitor ? t("activity.monitorInput") : t("activity.humanInput");
  const by = monitor
    ? t("activity.sentBy", { name: command?.createdBy === who.ownerId ? who.ownerName : t("activity.someone") })
    : t("activity.typedBy", { name: who.ownerName });
  const statuses: Record<string, string> = dict.activity.status;
  return (
    <li className={`act act-input act-${item.cls}${item.pending ? " act-pending" : ""}`}>
      <div className="act-head">
        <span className="act-icon" aria-hidden>{monitor ? ICON.monitor : ICON.human}</span>
        <span className="act-label">{label}</span>
        <span className="act-by">{by}</span>
        {command ? <span className={`badge status-${command.status}`}>{statuses[command.status] ?? command.status}</span> : null}
        <Truncated item={item} />
        <span className="act-time">{moment(item)}</span>
      </div>
      <Pressable item={item} open={open} onToggle={onToggle} className="act-text">
        {stop ? <><span aria-hidden>■ </span>{t("activity.stopRequest")}</> : item.text}
      </Pressable>
      {item.withHuman ? <div className="act-note">{t("activity.deliveredWith")}</div> : null}
      <Details item={item} open={open} />
    </li>
  );
}

/** Claude's own words: normal weight, a light stripe on the left. */
export function AssistantLine({ item, open, onToggle }: Omit<LineProps, "who">) {
  const { t } = useI18n();
  return (
    <li className="act act-assistant">
      <Pressable item={item} open={open} onToggle={onToggle} className="act-text">
        <span className="act-by">{t("activity.assistantLabel")} · {moment(item)}</span>
        <span className="act-clamp">{item.text}</span>
        <Truncated item={item} />
      </Pressable>
      <Details item={item} open={open} />
    </li>
  );
}

/** A tool call or a background event: one soft line. */
export function QuietLine({ item, open, onToggle }: Omit<LineProps, "who">) {
  return (
    <li className="act act-quiet">
      <Pressable item={item} open={open} onToggle={onToggle} className="act-quiet-line">
        <span className="act-time">{moment(item)}</span>
        <span className="act-kind">{item.event ? kindLabel(item.event.kind) : ""}</span>
        <span className="act-summary">{item.text}</span>
        <Truncated item={item} />
      </Pressable>
      <Details item={item} open={open} />
    </li>
  );
}

/** A run of quiet lines folded into one, closed until pressed. */
export function GroupLine({ row, open, onToggle, children }: {
  row: Extract<Row, { kind: "group" }>; open: boolean; onToggle: (key: string) => void; children: ReactNode;
}) {
  const { t } = useI18n();
  const parts = [
    row.tools > 0 ? t("activity.toolCalls", { n: row.tools }) : null,
    row.others > 0 ? t("activity.otherEvents", { n: row.others }) : null,
  ].filter(Boolean);
  return (
    <li className="act act-group">
      <button type="button" className="act-group-toggle" onClick={() => onToggle(row.key)} aria-expanded={open}>
        <span aria-hidden>{open ? "▾" : "▸"}</span> {parts.join(" · ")}
      </button>
      {open ? <ol className="act-list">{children}</ol> : null}
    </li>
  );
}
