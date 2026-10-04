import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { EventRow } from "../api/types";
import { Button, Card, Json } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { time } from "../lib/format";

/** A one-line summary of an event, from the fields Claude Code's hooks carry. */
export function summary(e: EventRow): string {
  const p = e.payload;
  const str = (k: string) => (typeof p[k] === "string" ? (p[k] as string) : "");
  const tool = str("tool_name");
  const input = p.tool_input as Record<string, unknown> | undefined;
  const detail = input ? String(input.command ?? input.file_path ?? input.description ?? input.pattern ?? "") : "";
  switch (e.kind) {
    case "hook:UserPromptSubmit":
      return str("prompt");
    case "hook:PreToolUse":
    case "hook:PostToolUse":
    case "hook:PostToolUseFailure":
    case "hook:PermissionRequest":
      return [tool, detail].filter(Boolean).join(" · ");
    case "hook:Notification":
      return str("message");
    case "hook:SubagentStart":
    case "hook:SubagentStop":
      return str("agent_type");
    case "note":
      return str("text");
    case "transcript": {
      const message = p.message as { content?: unknown } | undefined;
      const content = Array.isArray(message?.content) ? message.content : [];
      const text = content.find((c: { type?: string }) => c?.type === "text") as { text?: string } | undefined;
      return text?.text ?? str("type");
    }
    default:
      return "";
  }
}

const label = (kind: string) => kind.replace(/^hook:/, "");

/** The session's activity, newest first, an older page at a time. */
export function EventsCard({ sessionId, version }: { sessionId: string; version: number }) {
  const { t } = useI18n();
  const [rows, setRows] = useState<EventRow[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [open, setOpen] = useState<number | null>(null);

  const load = useCallback(async () => {
    const page = await api.events(sessionId, { limit: config.eventPageSize });
    setRows(page.items);
    setNext(page.next);
  }, [sessionId]);

  useEffect(() => {
    load().catch(() => setRows([]));
  }, [load, version]);

  const older = async () => {
    const page = await api.events(sessionId, { before: next, limit: config.eventPageSize });
    setRows((r) => [...r, ...page.items]);
    setNext(page.next);
  };

  return (
    <Card title={t("session.events")}>
      {rows.length === 0 ? <p className="muted">{t("session.noEvents")}</p> : (
        <ol className="timeline">
          {rows.map((e) => (
            <li key={e.id} className={`event event-${label(e.kind).toLowerCase()}`}>
              <button type="button" className="event-line" onClick={() => setOpen(open === e.id ? null : e.id)} aria-expanded={open === e.id}>
                <span className="event-time">{time(e.occurredAt)}</span>
                <span className="event-kind">{label(e.kind)}</span>
                <span className="event-summary">{summary(e)}</span>
                {e.truncated ? <span className="badge">{t("session.truncated")}</span> : null}
              </button>
              {open === e.id ? <Json value={e.payload} /> : null}
            </li>
          ))}
        </ol>
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void older()}>{t("session.olderEvents")}</Button></div> : null}
    </Card>
  );
}
