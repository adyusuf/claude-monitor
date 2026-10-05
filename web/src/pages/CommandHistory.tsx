import type { CommandRow } from "../api/types";
import { config } from "../config";
import { useI18n } from "../i18n";
import { dateTime, remaining } from "../lib/format";
import { CommandReply } from "./CommandReply";

/** True when nothing has happened in the session for longer than config.idleSessionMinutes. */
export function isIdle(lastEventAt: string, now: number): boolean {
  const last = new Date(lastEventAt).getTime();
  return Number.isFinite(last) && now - last > config.idleSessionMinutes * 60_000;
}

/** One command sent from the web: what it is, where it stands, and WHY it waits or lapsed. */
export function CommandItem({ command: c, sessionId, ended, now, canCancel, onCancel }: {
  command: CommandRow; sessionId: string; ended: boolean; now: number; canCancel: boolean; onCancel: (id: string) => void;
}) {
  const { t, dict } = useI18n();
  const left = c.status === "queued" || c.status === "delivered" ? remaining(c.expiresAt, now, dict.time) : null;
  const hint = c.status === "queued" ? t("commandHint.queued")
    : c.status === "delivered" ? t("commandHint.delivered")
      : c.status === "expired" ? t(c.deliveredAt ? "commandHint.expiredIdle" : "commandHint.expiredUnreached")
        : null;
  return (
    <li>
      <span className={`badge status-${c.status}`}>{t(`commandStatus.${c.status}` as never)}</span>
      <span>{c.kind === "stop" ? t("session.stop") : c.body}</span>
      <span className="muted small">{dateTime(c.createdAt)}</span>
      {canCancel && c.status === "queued" ? <button type="button" className="link" onClick={() => onCancel(c.id)}>{t("session.cancel")}</button> : null}
      {hint ? <div className="muted small command-hint">{hint}{left ? ` · ${t("session.expiresIn", { time: left })}` : ""}</div> : null}
      <CommandReply command={c} sessionId={sessionId} ended={ended} now={now} />
    </li>
  );
}
