import { useState } from "react";
import { api } from "../api/endpoints";
import type { ScheduleView, WebJobView } from "../api/types";
import { Button, Notice } from "../components/ui";
import { VisibleText } from "../components/VisibleText";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { useCodeGate } from "./useCodeGate";

const pad = (n: number) => String(n).padStart(2, "0");

/** "09:30" from minutes after midnight UTC. */
export const clock = (minutes: number): string => `${pad(Math.floor(minutes / 60))}:${pad(minutes % 60)}`;

/** Minutes after midnight from "HH:MM"; null for anything else. */
export const minutesOf = (text: string): number | null => {
  const match = /^(\d{1,2}):(\d{2})$/.exec(text.trim());
  if (!match) return null;
  const minutes = Number(match[1]) * 60 + Number(match[2]);
  return Number(match[1]) < 24 && Number(match[2]) < 60 ? minutes : null;
};

function ScheduleRow({ schedule: s, onChanged }: { schedule: ScheduleView; onChanged: () => void }) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const [notice, setNotice] = useState<string | null>(null);
  const simple = async (call: () => Promise<void>) => {
    setNotice(null);
    try {
      await call();
      onChanged();
    } catch (e) {
      setNotice(errorText(e));
    }
  };
  const when = s.kind === "daily" ? t("schedules.daily", { time: clock(s.dailyAtMinutes ?? 0) }) : t("schedules.every", { n: s.intervalMinutes ?? 0 });
  return (
    <li className="grant-row">
      <div className="run-line">
        <span className={`badge schedule-${s.status}`}>{tx(`schedules.status.${s.status}`)}</span>
        <strong><VisibleText text={s.jobName} /></strong>
        <span className="muted small">{when}</span>
      </div>
      <dl className="facts">
        {s.nextRunAt ? <><dt>{t("schedules.next")}</dt><dd>{dateTime(s.nextRunAt)}</dd></> : null}
        {s.lastRunAt ? <><dt>{t("schedules.last")}</dt><dd>{dateTime(s.lastRunAt)}{s.lastRunStatus ? ` · ${tx(`remote.status.${s.lastRunStatus}`)}` : ""}</dd></> : null}
        {s.failuresInARow > 0 ? <><dt>{t("schedules.failures")}</dt><dd>{s.failuresInARow}</dd></> : null}
        {s.pauseReason ? <><dt>{t("schedules.why")}</dt><dd>{tx(`schedules.reason.${s.pauseReason}`)}</dd></> : null}
      </dl>
      {gate.field}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      {notice ? <Notice kind="error">{notice}</Notice> : null}
      {s.status !== "ended" && s.isOwner ? (
        <div className="permission-actions">
          {s.status === "active" ? <Button variant="ghost" onClick={() => void simple(() => api.pauseSchedule(s.id))}>{t("schedules.pause")}</Button> : null}
          {s.status === "paused" ? <Button busy={gate.busy} onClick={() => void gate.attempt((code) => api.resumeSchedule(s.id, code)).then((ok) => ok && onChanged())}>{t("schedules.resume")}</Button> : null}
          <Button variant="danger" onClick={() => void simple(() => api.endSchedule(s.id))}>{t("schedules.end")}</Button>
        </div>
      ) : null}
    </li>
  );
}

/** Starts a schedule for one of the machine's active jobs. The owner confirms like a grant (a recent sign-in or a code). */
function NewSchedule({ jobs, onChanged }: { jobs: WebJobView[]; onChanged: () => void }) {
  const { t } = useI18n();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const usable = jobs.filter((j) => j.status === "active" && !j.argv.some((a) => a.includes("{")));
  const [jobId, setJobId] = useState("");
  const [kind, setKind] = useState<"interval" | "daily">("interval");
  const [minutes, setMinutes] = useState("60");
  const [time, setTime] = useState("03:00");
  const [problem, setProblem] = useState<string | null>(null);
  if (usable.length === 0) return <p className="muted small">{t("schedules.noJobs")}</p>;
  const chosen = jobId || usable[0]!.id;
  const create = async () => {
    setProblem(null);
    const interval = Number(minutes);
    const daily = minutesOf(time);
    if (kind === "interval" && !(Number.isInteger(interval) && interval >= 15 && interval <= 10080)) return setProblem(t("errors.invalid_schedule"));
    if (kind === "daily" && daily === null) return setProblem(t("errors.invalid_schedule"));
    const body = kind === "interval" ? { jobId: chosen, kind, intervalMinutes: interval } : { jobId: chosen, kind, dailyAtMinutes: daily! };
    if (await gate.attempt((code) => api.createSchedule({ ...body, code }).then(() => undefined))) onChanged();
  };
  return (
    <div className="stack">
      <p className="small muted">{t("schedules.newHint")}</p>
      <label className="field"><span>{t("schedules.job")}</span>
        <select className="input" value={chosen} onChange={(e) => setJobId(e.target.value)}>
          {usable.map((j) => <option key={j.id} value={j.id}>{j.name}</option>)}
        </select>
      </label>
      <label className="field"><span>{t("schedules.kind")}</span>
        <select className="input" value={kind} onChange={(e) => setKind(e.target.value as "interval" | "daily")}>
          <option value="interval">{t("schedules.kindInterval")}</option>
          <option value="daily">{t("schedules.kindDaily")}</option>
        </select>
      </label>
      {kind === "interval"
        ? <label className="field"><span>{t("schedules.minutes")}</span><input className="input" type="number" min={15} max={10080} value={minutes} onChange={(e) => setMinutes(e.target.value)} /></label>
        : <label className="field"><span>{t("schedules.timeUtc")}</span><input className="input" value={time} placeholder="03:00" onChange={(e) => setTime(e.target.value)} /></label>}
      {gate.field}
      {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      {problem ? <Notice kind="error">{problem}</Notice> : null}
      <div><Button busy={gate.busy} onClick={() => void create()}>{t("schedules.create")}</Button></div>
    </div>
  );
}

/** A machine's schedules (ADR-0010 §3): approved jobs that run by themselves, with their next run, last result and why one stopped. */
export function ScheduleList({ schedules, jobs, canCreate, onChanged }: { schedules: ScheduleView[]; jobs: WebJobView[]; canCreate: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  return (
    <div className="stack">
      {schedules.length === 0 ? <p className="muted">{t("schedules.none")}</p> : <ul className="plain run-list">{schedules.map((s) => <ScheduleRow key={s.id} schedule={s} onChanged={onChanged} />)}</ul>}
      {canCreate ? <NewSchedule jobs={jobs} onChanged={onChanged} /> : null}
    </div>
  );
}
