import { useState } from "react";
import { api } from "../api/endpoints";
import type { EstimationUnit, ItemType } from "../api/typesProjects";
import type { SprintSummary } from "../api/typesSprints";
import { Button, Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { sprintLimits } from "../lib/sprintOptions";
import { TypeBadge, useEstimateText } from "./IssueBits";
import { SprintQuickCreate } from "./SprintQuickCreate";
import { useSprintCandidates } from "./useSprintCandidates";

/** Puts issues into a planned or active sprint: pick several from the backlog (of the sprint's scope), or create a new one and add it at once. */
export function SprintAddIssues({ sprint, unit, itemTypes, version, onChanged }: {
  sprint: SprintSummary; unit: EstimationUnit; itemTypes: ItemType[]; version: number; onChanged: () => Promise<void>;
}) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const estimate = useEstimateText();
  const list = useSprintCandidates(sprint.projectId, sprint.applicationId, version);
  const [picked, setPicked] = useState<string[]>([]);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const toggle = (id: string) => setPicked((p) => p.includes(id) ? p.filter((x) => x !== id) : [...p, id]);

  const add = async () => {
    setBusy(true);
    try {
      setFailure(null);
      await api.addSprintIssues(sprint.id, picked);
      setPicked([]);
      await onChanged();
    } catch (e) {
      setFailure(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t("sprints.addTitle")}>
      <div className="stack">
        {failure ?? list.error ? <Notice kind="error">{failure ?? list.error}</Notice> : null}
        <h3 className="subhead">{t("sprints.candidates")}</h3>
        {list.rows === null ? <Spinner /> : list.rows.length === 0 ? <p className="muted">{t("sprints.noCandidates")}</p> : (
          <ul className="plain">
            {list.rows.map((issue) => {
              const mark = `#${issue.number}`;
              const on = picked.includes(issue.id);
              return (
                <li key={issue.id}>
                  <label className="check candidate">
                    <input type="checkbox" aria-label={t("sprints.selectIssue", { mark })} checked={on} disabled={!on && picked.length >= sprintLimits.addMax}
                      onChange={() => toggle(issue.id)} />
                    <span className="issue-number">{mark}</span>
                    <span>{issue.title}</span>
                    <TypeBadge type={issue.type} />
                    <span className="muted small">{estimate(issue.estimate, unit)}</span>
                  </label>
                </li>
              );
            })}
          </ul>
        )}
        {list.next ? <div className="more"><Button variant="ghost" onClick={() => void list.more()}>{t("projects.more")}</Button></div> : null}
        <div className="row-actions">
          <Button busy={busy} disabled={picked.length === 0} onClick={() => void add()}>{t("sprints.addSelected", { n: picked.length })}</Button>
        </div>
        <h3 className="subhead">{t("sprints.createTitle")}</h3>
        <SprintQuickCreate sprint={sprint} itemTypes={itemTypes} onChanged={onChanged} />
      </div>
    </Card>
  );
}
