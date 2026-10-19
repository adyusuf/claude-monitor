import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ProjectSummary } from "../api/typesProjects";
import { issueLimits } from "../lib/issueOptions";

/**
 * The workspace's active projects for a picker (the first page). With bugOnly, only those that take bugs: a project's item types are on the
 * project itself, not in the list, so each one is read once; one that cannot be read is left out. `projects` is null while loading.
 */
export function useRunProjects(ws: string, bugOnly: boolean): { projects: ProjectSummary[] | null; error: unknown } {
  const [state, setState] = useState<{ projects: ProjectSummary[] | null; error: unknown }>({ projects: null, error: null });

  useEffect(() => {
    let live = true;
    (async () => {
      const page = await api.projects(ws, { limit: issueLimits.projectsLimit });
      if (!bugOnly) return page.items;
      const views = await Promise.allSettled(page.items.map((p) => api.project(p.id)));
      return page.items.filter((_, i) => {
        const view = views[i];
        return view?.status === "fulfilled" && view.value.itemTypes.includes("bug");
      });
    })().then(
      (projects) => live && setState({ projects, error: null }),
      (error: unknown) => live && setState({ projects: null, error }),
    );
    return () => {
      live = false;
    };
  }, [ws, bugOnly]);

  return state;
}
