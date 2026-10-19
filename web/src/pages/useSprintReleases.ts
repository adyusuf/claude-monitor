import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ApplicationSummary } from "../api/typesProjects";
import { useErrorText } from "../i18n";

export interface ReleaseOption { id: string; label: string }

interface State { key: string; options: ReleaseOption[]; error: string | null }

/**
 * The releases a sprint may contribute to: those of its application, or (for a project-wide sprint) those of every live application of the
 * project, each named with its application. Cancelled releases are left out (the API refuses them).
 */
export function useSprintReleases(applications: ApplicationSummary[], applicationId: string, enabled: boolean) {
  const errorText = useErrorText();
  const [state, setState] = useState<State>({ key: "", options: [], error: null });
  const scope = applicationId ? applications.filter((a) => a.id === applicationId) : applications.filter((a) => a.status === "active");
  const key = enabled ? scope.map((a) => a.id).join(",") : "";

  useEffect(() => {
    if (!key) return;
    let live = true;
    Promise.all(scope.map(async (a) => (await api.releases(a.id)).filter((r) => r.status !== "cancelled").map((r) => ({ app: a, release: r })))).then(
      (groups) => live && setState({
        key, error: null,
        options: groups.flat().map(({ app, release }) => ({ id: release.id, label: applicationId ? release.name : `${app.name} · ${release.name}` })),
      }),
      (e: unknown) => live && setState({ key, options: [], error: errorText(e) }),
    );
    return () => {
      live = false;
    };
    // scope follows from key; errorText is a fresh function each render and loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  return state.key === key && key ? state : { key, options: [] as ReleaseOption[], error: null };
}
