import type { MachineProjectView, ProjectRunSummary } from "../api/types";
import { Empty } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";

/** "41 passed, 1 failed, 2 skipped", or an empty string when the test tool was not recognised. */
export function testCounts(r: ProjectRunSummary, t: (key: "machines.projects.passed" | "machines.projects.failed" | "machines.projects.skipped", v: { n: number }) => string): string {
  if (r.testsPassed === null && r.testsFailed === null && r.testsSkipped === null) return "";
  return [t("machines.projects.passed", { n: r.testsPassed ?? 0 }), t("machines.projects.failed", { n: r.testsFailed ?? 0 }),
    ...(r.testsSkipped ? [t("machines.projects.skipped", { n: r.testsSkipped })] : [])].join(", ");
}

const resultClass = (r: ProjectRunSummary): string => (r.status === "succeeded" ? "badge status-active" : "badge status-failed");

function ProjectRow({ p }: { p: MachineProjectView }) {
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
export function MachineProjects({ projects }: { projects: MachineProjectView[] }) {
  const { t } = useI18n();
  if (projects.length === 0) return <Empty title={t("machines.projects.none")} hint={t("machines.projects.noneHint")} />;
  return <ul className="stack">{projects.map((p) => <ProjectRow key={p.key} p={p} />)}</ul>;
}
