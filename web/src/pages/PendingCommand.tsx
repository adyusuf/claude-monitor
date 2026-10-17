import type { CommandRow } from "../api/types";
import { useI18n } from "../i18n";
import { time } from "../lib/format";
import { Markdown } from "../lib/markdown";
import { useCommandState } from "./CommandHistory";

/** Commands that are not in the session yet (or never will be): waiting, delivered, expired, failed or cancelled. Applied ones are the session's own messages. */
export const isPending = (c: CommandRow) => c.status !== "applied";

/**
 * A message sent from the web that the session has not shown yet, in the conversation where it will appear: the text as it was typed
 * and, under it, where it stands (the same words as the history). An applied command is not drawn: the session's own line replaces it.
 */
export function PendingCommand({ command: c, now, canCancel, onCancel }: { command: CommandRow; now: number; canCancel: boolean; onCancel: (id: string) => void }) {
  const { t } = useI18n();
  const { hint } = useCommandState(c, now);
  return (
    <li className={`chat-row chat-user chat-pending chat-pending-${c.status}`} data-kind="command" data-status={c.status}>
      <div className="chat-bubble">
        {c.kind === "stop" ? <p><strong>{t("session.stop")}</strong></p> : <Markdown text={c.body ?? ""} />}
        <div className="chat-pending-state muted small">
          <span className={`badge status-${c.status}`}>{t(`commandStatus.${c.status}` as never)}</span>
          {hint ? <span className="command-hint"> {hint}</span> : null}
          {canCancel && c.status === "queued" ? <> <button type="button" className="link" onClick={() => onCancel(c.id)}>{t("session.cancel")}</button></> : null}
          <time className="chat-time" dateTime={c.createdAt}> {time(c.createdAt)}</time>
        </div>
      </div>
    </li>
  );
}
