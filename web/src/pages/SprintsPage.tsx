import { useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import type { SprintStatus, SprintSummary } from "../api/typesSprints";
import { Button, Card, Empty, Notice, Spinner } from "../components/ui";
import { useI18n } from "../i18n";
import { emptySprintDraft, inputOfSprint, teamCandidates, type SprintDraft } from "../lib/sprintForm";
import { SPRINT_STATUSES } from "../lib/sprintOptions";
import { SprintCard } from "./SprintCard";
import { SprintForm } from "./SprintForm";
import { useSprintContext } from "./useSprintContext";
import { useSprintReleases } from "./useSprintReleases";
import { useSprints, type SprintFilter } from "./useSprints";

const FILTERS: SprintFilter[] = ["all", ...SPRINT_STATUSES];
/** With every status shown, the sprints sit in groups in this order: what runs, what is next, what is over. */
const GROUPS: SprintStatus[] = ["active", "planned", "closed"];

/** The sprints of a project, or of one application, as cards by status; those who may add sprints get a form for a new one and Start and Delete on a planned card. */
export function SprintsPage() {
  const { ws = "", projectId = "", appId = "" } = useParams();
  const { t } = useI18n();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const [filter, setFilter] = useState<SprintFilter>("all");
  const context = useSprintContext(ws, projectId, appId);
  const list = useSprints(projectId, appId, filter);
  const creating = params.get("new") === "1";
  const releases = useSprintReleases(context.applications, appId, creating);
  const { project } = context;

  const setCreating = (on: boolean) => setParams(on ? { new: "1" } : {}, { replace: true });
  const create = async (draft: SprintDraft) => {
    const created = await api.createSprint(projectId, inputOfSprint(draft, appId || null));
    navigate(`/w/${ws}/projects/${projectId}/sprints/${created.sprint.id}`);
  };
  const remove = (s: SprintSummary) => {
    if (window.confirm(t("sprints.deleteConfirm", { n: s.number }))) void list.remove(s);
  };

  if (!project) return context.error ? <div className="page"><Notice kind="error">{context.error}</Notice></div> : <Spinner />;
  const canWrite = project.access.canEditApplications && project.status === "active";
  const owner = context.application?.name ?? project.name;
  const back = appId ? `/w/${ws}/projects/${projectId}/applications/${appId}` : `/w/${ws}/projects/${projectId}`;
  const error = context.error ?? list.error ?? releases.error;
  const scopeName = (s: SprintSummary) => context.applications.find((a) => a.id === s.applicationId)?.name ?? null;
  const card = (s: SprintSummary) => (
    <SprintCard key={s.id} ws={ws} sprint={s} unit={project.estimationUnit} scopeName={scopeName(s)} canWrite={canWrite} onStart={(x) => void list.start(x)} onDelete={remove} />
  );
  const groups = GROUPS.map((status) => ({ status, items: (list.rows ?? []).filter((s) => s.status === status) })).filter((g) => g.items.length > 0);

  return (
    <div className="page">
      <Link className="back" to={back}>← {owner}</Link>
      <header className="page-head">
        <h1>{t("sprints.title")} <span className="muted">· {owner}</span></h1>
        <div className="row-actions" role="group" aria-label={t("sprints.filter")}>
          {FILTERS.map((f) => (
            <Button key={f} variant={filter === f ? "primary" : "ghost"} aria-pressed={filter === f} onClick={() => setFilter(f)}>
              {f === "all" ? t("sprints.all") : t(`sprints.status.${f}`)}
            </Button>
          ))}
          {canWrite && !creating ? <Button onClick={() => setCreating(true)}>{t("sprints.new")}</Button> : null}
        </div>
      </header>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {creating && canWrite ? (
        <Card title={t("sprints.new")}>
          <SprintForm initial={emptySprintDraft(project)} project={project} people={teamCandidates(context.members, [])} releases={releases.options}
            submitLabel={t("sprints.create")} onSubmit={create} onCancel={() => setCreating(false)} />
        </Card>
      ) : null}
      {list.rows === null ? <Spinner /> : list.rows.length === 0 ? (
        <Empty title={filter === "all" ? t("sprints.empty") : t("sprints.emptyFiltered")} hint={filter === "all" && canWrite ? t("sprints.emptyHint") : undefined} />
      ) : filter === "all" ? groups.map((g) => (
        <section key={g.status} className="stack">
          <h2 className="subhead">{t(`sprints.status.${g.status}`)}</h2>
          <div className="tiles">{g.items.map(card)}</div>
        </section>
      )) : <div className="tiles">{list.rows.map(card)}</div>}
      {list.next ? <div className="more"><Button variant="ghost" onClick={() => void list.more()}>{t("projects.more")}</Button></div> : null}
    </div>
  );
}
