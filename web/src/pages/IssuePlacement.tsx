import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { Member } from "../api/types";
import type { IssueDraft } from "../lib/issueForm";
import type { ApplicationSummary } from "../api/typesProjects";
import type { IssueSummary } from "../api/typesIssues";
import { useErrorText, useI18n } from "../i18n";
import { issueLimits } from "../lib/issueOptions";
import { Notice } from "../components/ui";
import { Labeled, workers } from "./ProjectBits";
import { useApplicationParts } from "./useApplicationParts";

/** The epics a story can sit under (never the issue itself); read once when the form opens. */
function useEpics(projectId: string, selfId: string) {
  const errorText = useErrorText();
  const [state, setState] = useState<{ epics: IssueSummary[]; error: string | null }>({ epics: [], error: null });
  useEffect(() => {
    let live = true;
    api.issues(projectId, { type: "epic", limit: issueLimits.epicLimit }).then(
      (page) => live && setState({ epics: page.items.filter((x) => x.id !== selfId), error: null }),
      (e: unknown) => live && setState({ epics: [], error: errorText(e) }),
    );
    return () => {
      live = false;
    };
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, selfId]);
  return state;
}

/** Where an issue belongs: who works on it, its epic, its application and, from that application, its component and release. */
export function IssuePlacement({ draft, initial, projectId, issueId, members, applications, errors, onChange }: {
  draft: IssueDraft; initial: IssueDraft; projectId: string; issueId: string; members: Member[]; applications: ApplicationSummary[];
  errors: (field: string) => string | undefined; onChange: (patch: Partial<IssueDraft>) => void;
}) {
  const { t } = useI18n();
  const parts = useApplicationParts(draft.applicationId);
  const { epics, error } = useEpics(projectId, issueId);
  const none = <option value="">–</option>;
  return (
    <>
      {error || parts.error ? <Notice kind="error">{error ?? parts.error}</Notice> : null}
      <div className="form-grid">
        <Labeled label={t("issues.assignee")} error={errors("assigneeUserId")}>
          {(id) => (
            <select id={id} className="input" value={draft.assigneeUserId} onChange={(e) => onChange({ assigneeUserId: e.target.value })}>
              {none}
              {workers(members, initial.assigneeUserId).map((m) => <option key={m.userId} value={m.userId}>{m.displayName}</option>)}
            </select>
          )}
        </Labeled>
        {draft.type === "epic" ? null : (
          <Labeled label={t("issues.parent")} error={errors("parentId")}>
            {(id) => (
              <select id={id} className="input" value={draft.parentId} onChange={(e) => onChange({ parentId: e.target.value })}>
                {none}
                {epics.map((x) => <option key={x.id} value={x.id}>{`#${x.number} ${x.title}`}</option>)}
              </select>
            )}
          </Labeled>
        )}
        <Labeled label={t("issues.application")} error={errors("applicationId")}>
          {(id) => (
            <select id={id} className="input" value={draft.applicationId} onChange={(e) => onChange({ applicationId: e.target.value, componentId: "", releaseId: "" })}>
              {none}
              {applications.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("issues.component")} error={errors("componentId")}>
          {(id) => (
            <select id={id} className="input" value={draft.componentId} disabled={!draft.applicationId} onChange={(e) => onChange({ componentId: e.target.value })}>
              {none}
              {parts.components.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          )}
        </Labeled>
        <Labeled label={t("issues.release")} error={errors("releaseId")}>
          {(id) => (
            <select id={id} className="input" value={draft.releaseId} disabled={!draft.applicationId} onChange={(e) => onChange({ releaseId: e.target.value })}>
              {none}
              {parts.releases.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
            </select>
          )}
        </Labeled>
      </div>
    </>
  );
}
