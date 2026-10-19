import { useCallback, useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ProjectInput, ProjectSummary } from "../api/typesProjects";
import { useSession } from "../auth/session";
import { Button, Card, Empty, Notice, Spinner } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { emptyProjectDraft } from "../lib/projectForm";
import { canWorkInProjects } from "../lib/projectOptions";
import { ProjectCard } from "./ProjectCard";
import { ProjectForm } from "./ProjectForm";

/** The workspace's managed projects as cards, with search, archived ones on request and (for members and above) a form for a new one. */
export function ProjectsPage() {
  const { ws = "" } = useParams();
  const { t } = useI18n();
  const errorText = useErrorText();
  const navigate = useNavigate();
  const { me } = useSession();
  const canWork = canWorkInProjects(me?.workspaces.find((w) => w.id === ws)?.role);
  const [rows, setRows] = useState<ProjectSummary[] | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [archived, setArchived] = useState(false);
  const [members, setMembers] = useState<Member[] | null>(null);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const status = archived ? "all" : undefined;

  const load = useCallback(async () => {
    try {
      const page = await api.projects(ws, { q: query, status, limit: config.pageSize });
      setRows(page.items);
      setNext(page.next);
      setError(null);
    } catch (e) {
      setError(e);
    }
  }, [ws, query, status]);

  useEffect(() => {
    const timer = setTimeout(() => void load(), query ? config.liveDebounceMs : 0);
    return () => clearTimeout(timer);
  }, [load, query]);

  const act = async (work: () => Promise<unknown>) => {
    try {
      await work();
      await load();
    } catch (e) {
      setError(e);
    }
  };

  const more = async () => {
    try {
      const page = await api.projects(ws, { q: query, status, cursor: next, limit: config.pageSize });
      setRows((r) => [...(r ?? []), ...page.items]);
      setNext(page.next);
    } catch (e) {
      setError(e);
    }
  };

  const startCreating = async () => {
    try {
      setMembers(await api.members(ws));
      setCreating(true);
    } catch (e) {
      setError(e);
    }
  };

  const create = async (input: ProjectInput) => {
    const created = await api.createProject(ws, input);
    navigate(`/w/${ws}/projects/${created.id}`);
  };

  const archive = (p: ProjectSummary) => {
    if (window.confirm(t("projects.archiveConfirm", { name: p.name }))) void act(() => api.archiveProject(p.id));
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>{t("projects.title")}</h1>
        {canWork && !creating ? <Button onClick={() => void startCreating()}>{t("projects.new")}</Button> : null}
      </header>
      {error ? <Notice kind="error">{errorText(error)}</Notice> : null}
      {creating && members && me ? (
        <Card title={t("projects.new")}>
          <ProjectForm initial={emptyProjectDraft(me.id)} members={members} canChangeOwner submitLabel={t("projects.create")}
            onSubmit={create} onCancel={() => setCreating(false)} />
        </Card>
      ) : null}
      <div className="toolbar">
        <input className="input search" type="search" placeholder={t("projects.search")} aria-label={t("projects.search")}
          value={query} onChange={(e) => setQuery(e.target.value)} />
        <label className="check">
          <input type="checkbox" checked={archived} onChange={(e) => setArchived(e.target.checked)} />
          <span>{t("projects.showArchived")}</span>
        </label>
      </div>
      {rows === null ? <Spinner /> : rows.length === 0 ? (
        <Empty title={query ? t("projects.noMatch") : t("projects.empty")} hint={query || !canWork ? undefined : t("projects.emptyHint")} />
      ) : (
        <div className="tiles">
          {rows.map((p) => (
            <ProjectCard key={p.id} ws={ws} project={p} canWork={canWork} onArchive={archive} onRestore={(x) => void act(() => api.restoreProject(x.id))} />
          ))}
        </div>
      )}
      {next ? <div className="more"><Button variant="ghost" onClick={() => void more()}>{t("projects.more")}</Button></div> : null}
    </div>
  );
}
