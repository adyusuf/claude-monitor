import { useState } from "react";
import { api } from "../api/endpoints";
import type { CommandRow } from "../api/types";
import { config } from "../config";
import { useI18n } from "../i18n";

/** Where a command stands once it was handed to the session: not a stored status, a view over the recorded events. */
export type ReplyState = "waiting" | "answered" | "unrecorded";

/**
 * Only an applied prompt has one. A reply found means answered; with none, it is waiting for a while, then no reply
 * is recorded; and a session that ended with none owes nothing more.
 */
export function replyState(c: CommandRow, ended: boolean, now: number): ReplyState | null {
  if (c.kind !== "prompt" || c.status !== "applied") return null;
  if (c.replyEventId != null) return "answered";
  if (ended) return null;
  const applied = c.appliedAt ? new Date(c.appliedAt).getTime() : NaN;
  return Number.isFinite(applied) && now - applied > config.replyWaitMinutes * 60_000 ? "unrecorded" : "waiting";
}

/** Every text block of an assistant message, in order. */
export function messageText(payload: Record<string, unknown>): string {
  const message = payload.message as { content?: unknown } | undefined;
  const content = Array.isArray(message?.content) ? (message.content as { type?: string; text?: unknown }[]) : [];
  return content.filter((b) => b?.type === "text" && typeof b.text === "string").map((b) => b.text as string).join("\n\n");
}

/** Under a command: whether Claude has answered it, and the start of the answer (the whole of it on request). */
export function CommandReply({ command: c, sessionId, ended, now }: { command: CommandRow; sessionId: string; ended: boolean; now: number }) {
  const { t } = useI18n();
  const [full, setFull] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const state = replyState(c, ended, now);
  if (!state) return null;

  const toggle = async () => {
    if (open || full !== null) return setOpen(!open);
    setBusy(true);
    try {
      // The events list pages by "older than"; the event after the reply's id, one row, is the reply itself.
      const page = await api.events(sessionId, { before: String((c.replyEventId ?? 0) + 1), limit: 1 });
      setFull(messageText(page.items.find((e) => e.id === c.replyEventId)?.payload ?? {}));
      setOpen(true);
    } catch {
      setFull("");
      setOpen(true);
    } finally {
      setBusy(false);
    }
  };

  const shown = open && full !== null ? full || t("commandReply.gone") : `${c.replyText ?? ""}${c.replyMore ? "…" : ""}`;
  return (
    <div className="command-reply muted small">
      <span className={`reply-state reply-${state}`}>{t(`commandReply.${state}` as never)}</span>
      {state === "answered" ? (
        <>
          <blockquote>{shown}</blockquote>
          {c.replyMore ? (
            <button type="button" className="link" disabled={busy} onClick={() => void toggle()}>
              {busy ? t("commandReply.loading") : open ? t("commandReply.less") : t("commandReply.more")}
            </button>
          ) : null}
        </>
      ) : null}
    </div>
  );
}
