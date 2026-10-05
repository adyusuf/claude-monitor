import type { CommandRow, EventRow } from "../api/types";
import { config } from "../config";
import { ActivityClass, type ActivityClassName, classifyEvent, CommandKind, CommandStatus, EventKind, summary, TranscriptType, transcriptUserText } from "./activity";

/** One line of the Activity list: a session event, or a command sent from the web. */
export interface TimelineItem {
  key: string;
  cls: ActivityClassName;
  /** Milliseconds since the epoch: when it entered the session (a command: when it was applied, else created). */
  at: number;
  /** Orders items with the same `at`; a web command that rode along with a typed prompt sits directly above it. */
  tie: number;
  text: string;
  event?: EventRow;
  command?: CommandRow;
  /** A command that has not entered the session yet (queued or delivered). */
  pending: boolean;
  /** A web command applied together with a prompt typed in the session (it sits next to that prompt). */
  withHuman: boolean;
}

export const ActivityFilter = { All: "all", Inputs: "inputs", Assistant: "assistant", Tools: "tools" } as const;
export type ActivityFilterName = (typeof ActivityFilter)[keyof typeof ActivityFilter];
export const ACTIVITY_FILTERS: readonly ActivityFilterName[] = Object.values(ActivityFilter);

/** Which classes each filter shows. "Tools" also covers the quiet background events (usage, notices, subagents). */
const FILTER_CLASSES: Record<ActivityFilterName, readonly ActivityClassName[]> = {
  [ActivityFilter.All]: Object.values(ActivityClass),
  [ActivityFilter.Inputs]: [ActivityClass.MonitorInput, ActivityClass.HumanInput],
  [ActivityFilter.Assistant]: [ActivityClass.Assistant],
  [ActivityFilter.Tools]: [ActivityClass.Tool, ActivityClass.Meta],
};

export function isFilter(value: unknown): value is ActivityFilterName {
  return typeof value === "string" && (ACTIVITY_FILTERS as readonly string[]).includes(value);
}

export interface Timeline {
  /** Commands still waiting to enter the session: always on top, whatever page of activity is loaded. */
  pinned: TimelineItem[];
  /** Everything else, newest first. */
  items: TimelineItem[];
}

const ms = (iso: string | null | undefined) => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(t) ? t : 0;
};

const isPending = (c: CommandRow) => c.status === CommandStatus.Queued || c.status === CommandStatus.Delivered;

/** Every string anywhere in a payload (bounded depth): what a transcript line says, whatever its wrapper looks like. */
function strings(value: unknown, depth = 0, out: string[] = []): string[] {
  if (typeof value === "string") out.push(value);
  else if (depth < 6 && value !== null && typeof value === "object") {
    for (const v of Object.values(value)) strings(v, depth + 1, out);
  }
  return out;
}

/**
 * True for a transcript line that only echoes a web command: Claude Code writes the text it was given (as context
 * or as a stop-hook reason) into the transcript. The command itself is the authoritative record, so the echo is
 * dropped. Matching is by the command's own text near the moment it was applied, never by the wrapper's wording.
 */
function echoesCommand(e: EventRow, at: number, applied: { body: string; at: number }[]): boolean {
  const type = e.payload.type;
  if (e.kind !== EventKind.Transcript || (type !== TranscriptType.User && type !== TranscriptType.Attachment)) return false;
  const near = applied.filter((c) => Math.abs(at - c.at) <= config.commandEchoSeconds * 1000);
  if (near.length === 0) return false;
  const texts = strings(e.payload);
  return near.some((c) => texts.some((s) => s.includes(c.body)));
}

/**
 * Merges a session's events with its web commands into one list.
 *
 * - A web command is a MONITOR input; a typed prompt (hook:UserPromptSubmit) is a HUMAN input.
 * - A transcript "user" line with text repeats the prompt hook of the same turn: it shows only when no prompt hook
 *   with the same text is loaded for it (a harness without the hook), so a prompt never appears twice.
 * - A transcript line that echoes an applied web command is dropped (see echoesCommand).
 * - A command that is still waiting is pinned on top. Any other command is placed by its moment, but while older
 *   pages of events are unloaded, a command older than the oldest loaded event waits for "Older activity": it can
 *   never land in the wrong place between two pages.
 * - A command applied at the moment of a typed prompt sits directly above that prompt.
 */
export function buildTimeline(events: EventRow[], commands: CommandRow[], hasOlder: boolean): Timeline {
  const prompts = new Map<string, number>();
  for (const e of events) {
    if (e.kind === EventKind.Prompt) {
      const text = typeof e.payload.prompt === "string" ? e.payload.prompt.trim() : "";
      prompts.set(text, (prompts.get(text) ?? 0) + 1);
    }
  }
  const applied = commands
    .filter((c) => c.status === CommandStatus.Applied && c.body?.trim())
    .map((c) => ({ body: c.body!.trim(), at: ms(c.appliedAt ?? c.createdAt) }));

  const items: TimelineItem[] = [];
  let oldest = Number.POSITIVE_INFINITY;
  for (const e of events) {
    const at = ms(e.occurredAt);
    oldest = Math.min(oldest, at);
    if (echoesCommand(e, at, applied)) continue;
    const cls = classifyEvent(e);
    if (cls === ActivityClass.HumanInput && e.kind === EventKind.Transcript) {
      const text = transcriptUserText(e);
      const left = prompts.get(text) ?? 0;
      if (left > 0) {
        prompts.set(text, left - 1);
        continue;
      }
    }
    items.push({ key: `e${e.id}`, cls, at, tie: e.id, text: summary(e), event: e, pending: false, withHuman: false });
  }

  const humans = items.filter((i) => i.cls === ActivityClass.HumanInput);
  const pinned: TimelineItem[] = [];
  for (const c of commands) {
    const pending = isPending(c);
    const at = ms(c.appliedAt ?? c.createdAt);
    const item: TimelineItem = {
      key: `c${c.id}`, cls: ActivityClass.MonitorInput, at, tie: 0, pending, withHuman: false,
      text: c.kind === CommandKind.Stop ? "" : c.body ?? "", command: c,
    };
    if (pending) {
      pinned.push(item);
      continue;
    }
    if (hasOlder && at < oldest) continue;
    const mate = c.kind === CommandKind.Prompt && c.status === CommandStatus.Applied ? nearest(humans, at) : null;
    if (mate) {
      item.at = mate.at;
      item.tie = mate.tie + 0.5;
      item.withHuman = true;
    }
    items.push(item);
  }

  const newestFirst = (a: TimelineItem, b: TimelineItem) => b.at - a.at || b.tie - a.tie;
  return { pinned: pinned.sort(newestFirst), items: items.sort(newestFirst) };
}

function nearest(humans: TimelineItem[], at: number): TimelineItem | null {
  let best: TimelineItem | null = null;
  for (const h of humans) {
    const gap = Math.abs(h.at - at);
    if (gap <= config.commandPairSeconds * 1000 && (best === null || gap < Math.abs(best.at - at))) best = h;
  }
  return best;
}

export function filterItems(items: TimelineItem[], filter: ActivityFilterName): TimelineItem[] {
  const shown = FILTER_CLASSES[filter];
  return items.filter((i) => shown.includes(i.cls));
}

/** A line of the rendered list: one item, or a run of quiet tool/meta lines folded into one group. */
export type Row =
  | { kind: "item"; item: TimelineItem }
  | { kind: "group"; key: string; items: TimelineItem[]; tools: number; others: number };

const isQuiet = (i: TimelineItem) => i.cls === ActivityClass.Tool || i.cls === ActivityClass.Meta;

/** Folds consecutive tool/meta lines into groups; a lone quiet line stays a line. */
export function groupRows(items: TimelineItem[]): Row[] {
  const rows: Row[] = [];
  let run: TimelineItem[] = [];
  const flush = () => {
    if (run.length === 1) rows.push({ kind: "item", item: run[0]! });
    else if (run.length > 1) {
      const tools = run.filter((i) => i.cls === ActivityClass.Tool).length;
      rows.push({ kind: "group", key: `g${run[0]!.key}`, items: run, tools, others: run.length - tools });
    }
    run = [];
  };
  for (const item of items) {
    if (isQuiet(item)) run.push(item);
    else {
      flush();
      rows.push({ kind: "item", item });
    }
  }
  flush();
  return rows;
}
