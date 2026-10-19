import { useState } from "react";
import { api } from "../api/endpoints";
import type { MachineProjectView, ProjectRunSummary } from "../api/types";
import { Button, Empty, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";

/** "41 passed, 1 failed, 2 skipped", or an empty string when the test tool was not recognised. */
export function testCounts(r: ProjectRunSummary, t: (key: "machines.projects.passed" | "machines.projects.failed" | "machines.projects.skipped", v: { n: number }) => string): string {
  if (r.testsPassed === null && r.testsFailed === null && r.testsSkipped === null) return "";
  return [t("machines.projects.passed", { n: r.testsPassed ?? 0 }), t("machines.projects.failed", { n: r.testsFailed ?? 0 }),
    ...(r.testsSkipped ? [t("machines.projects.skipped", { n: r.testsSkipped })] : [])].join(", ");
}

const resultClass = (r: ProjectRunSummary): string => (r.status === "succeeded" ? "badge status-active" : "badge status-failed");

/** The test command of a project, set at the machine. The owner sees it and starts it; the run still waits for the owner's approval (ADR-0011 §7a). */
function ProjectTest({ p, agentId, onStarted }: { p: MachineProjectView; agentId: string; onStarted: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const start = async () => {
    setBusy(true);
    setError(null);
    setDone(false);
    try {
      await api.startProjectTest(agentId, p.key);
      setDone(true);
      onStarted();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="stack">
      <div className="row small">
        <span className="muted">{t("machines.projects.testCommand")}</span>
        <code>{(p.testArgv ?? []).join(" ")}</code>
        {p.testCwd ? <span className="muted">{t("machines.projects.testIn", { dir: p.testCwd })}</span> : null}
        <Button variant="subtle" busy={busy} onClick={() => void start()}>{t("machines.projects.startTest")}</Button>
      </div>
      {done ? <Notice kind="success">{t("machines.projects.testAsked")}</Notice> : null}
      {error ? <Notice kind="error">{error}</Notice> : null}
    </div>
  );
}

function ProjectRow({ p, agentId, owned, onStarted }: { p: MachineProjectView; agentId?: string; owned?: boolean; onStarted?: () => void }) {
  const { t } = useI18n();
  return (
    <li className="stack project-row">
      <div className="row">
        <strong>{p.name}</strong>
        <span className="muted small">{p.key}</span>
        {p.branch ? <span className="badge">{p.branch}</span> : null}
        {p.headSha ? <code className="small">{p.headSha.slice(0, 8)}</code> : null}
        {p.tag ? <span className="badge">{p.tag}</span> : null}
        {p.recentErrors > 0 ? <span className="badge warn">{t("machines.projects.errors", { n: p.recentErrors })}</span> : null}
        <span className="muted small">{p.source === "listed" ? t("machines.projects.listed") : t("machines.projects.session")} · {dateTime(p.lastSeenAt)}</span>
      </div>
      {owned && agentId && p.hasTest && p.testArgv ? <ProjectTest p={p} agentId={agentId} onStarted={onStarted ?? (() => undefined)} /> : null}
      {p.lastRuns.length > 0 ? (
        <ul className="run-lines">
          {p.lastRuns.map((r, i) => (
            <li key={`${r.at}-${i}`} className="row small">
              <span className="muted">{r.kind ? t(`machines.projects.kind.${r.kind as "test" | "build" | "deploy" | "other"}`) : t("machines.projects.run")}</span>
              <span className={resultClass(r)}>{r.status}{r.exitCode !== null ? ` (${r.exitCode})` : ""}</span>
              {testCounts(r, t) ? <span>{testCounts(r, t)}</span> : null}
              {r.outputTruncated ? <span className="muted">{t("machines.projects.cut")}</span> : null}
              <span className="muted">{dateTime(r.at)}</span>
            </li>
          ))}
        </ul>
      ) : <p className="muted small">{t("machines.projects.noRuns")}</p>}
    </li>
  );
}

/** The projects on a machine with their version and the last runs per kind (ADR-0008 §3a′); an older API sends none. */
export function MachineProjects({ projects, agentId, owned, onStarted }: { projects: MachineProjectView[]; agentId?: string; owned?: boolean; onStarted?: () => void }) {
  const { t } = useI18n();
  if (projects.length === 0) return <Empty title={t("machines.projects.none")} hint={t("machines.projects.noneHint")} />;
  return <ul className="stack">{projects.map((p) => <ProjectRow key={p.key} p={p} agentId={agentId} owned={owned} onStarted={onStarted} />)}</ul>;
}
