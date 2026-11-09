import { useState } from "react";
import { Link } from "react-router";
import type { ApprovalRequestRow } from "../api/types";
import { useI18n } from "../i18n";
import { ApprovalItem, previewOf } from "../pages/ApprovalItem";
import { Icon } from "./icons";
import { Modal } from "./Modal";
import { Button } from "./ui";

/** The first line of a preview and how many lines follow it: the band shows one line, so a command of several never reads as one. */
export function firstLine(text: string): [string, number] {
  const lines = text.split(/\r\n|\r|\n/);
  return [lines[0] ?? "", lines.length - 1];
}

/**
 * The band at the top of every workspace page (ADR-0019 §11): how many requests wait on the viewer's sessions, the oldest one in a line (tool,
 * preview, session, machine), and Details, which opens them all with Allow / Deny and how long an allow stands. Runs waiting for the viewer's
 * approval are counted and link to the runs page, where their own card decides them. Nothing waits: no band.
 */
export function ApprovalBand({ ws, rows, runs, onAnswered }: { ws: string; rows: ApprovalRequestRow[]; runs: number; onAnswered: (id: string) => void }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  if (rows.length === 0 && runs === 0) return null;
  const first = rows[0];
  const [preview, more] = firstLine(first ? previewOf(first) : "");
  return (
    <div className="approval-band" role="status">
      <Icon name="approvals" />
      {first ? (
        <span className="approval-band-text">
          <strong>{rows.length === 1 ? t("approvalRules.bandOne") : t("approvalRules.bandMany", { n: rows.length })}</strong>
          <span className="approval-band-first">
            {first.toolName}{preview ? <> <code>{preview}</code>{more > 0 ? <> {t("approvalRules.moreLines", { n: more })}</> : null}</> : null} · {first.sessionTitle ?? t("approvalRules.untitled")} · {first.hostname}
          </span>
        </span>
      ) : null}
      {runs > 0 ? <Link className="approval-band-runs" to={`/w/${ws}/runs`}>{t("approvalRules.bandRuns", { n: runs })}</Link> : null}
      {rows.length > 0 ? <Button variant="ghost" onClick={() => setOpen(true)}>{t("approvalRules.details")}</Button> : null}
      {open ? (
        <Modal title={t("approvalRules.dialog")} onClose={() => setOpen(false)}>
          {rows.length === 0 ? <p className="muted">{t("approvalRules.noneWaiting")}</p> : null}
          {rows.map((r) => <ApprovalItem key={r.id} ws={ws} row={r} onDone={onAnswered} />)}
          <p className="small"><Link to={`/w/${ws}/approvals`} onClick={() => setOpen(false)}>{t("approvalRules.title")}</Link></p>
        </Modal>
      ) : null}
    </div>
  );
}
