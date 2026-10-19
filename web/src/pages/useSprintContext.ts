import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { ApplicationSummary, ApplicationView, ProjectView } from "../api/typesProjects";
import { useErrorText } from "../i18n";

/** What the sprint pages show beside the sprints: the project (units, default length, rights), the workspace members for the team and the applications. */
export function useSprintContext(ws: string, projectId: string, applicationId: string) {
  const errorText = useErrorText();
  const [project, setProject] = useState<ProjectView | null>(null);
  const [members, setMembers] = useState<Member[]>([]);
  const [applications, setApplications] = useState<ApplicationSummary[]>([]);
  const [application, setApplication] = useState<ApplicationView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [view, people, apps, app] = await Promise.all([
        api.project(projectId), api.members(ws), api.applications(projectId, { status: "all" }), applicationId ? api.application(applicationId) : null,
      ]);
      setProject(view);
      setMembers(people);
      setApplications(apps.items);
      setApplication(app);
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws, projectId, applicationId]);

  useEffect(() => {
    void load();
  }, [load]);

  return { project, members, applications, application, error };
}
