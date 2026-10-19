import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { SprintSummary } from "../api/typesSprints";
import { Button, Card, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { Labeled } from "./ProjectBits";
import { useSprintTitle } from "./SprintBits";

/** The planned sprints of the same scope as this one (a project-wide sprint's are the project-wide ones), which can take its unfinished issues. */
function usePlannedTargets(sprint: SprintSummary) {
  const errorText = useErrorText();
  const [state, setState] = useState<{ targets: SprintSummary[]; error: string | null }>({ targets: [], error: null });
  useEffect(() => {
    let live = true;
    api.sprints(sprint.projectId, { status: "planned", application: sprint.applicationId ?? undefined, limit: config.pageSize }).then(
      (page) => live && setState({ targets: page.items.filter((s) => s.id !== sprint.id && s.applicationId === sprint.applicationId), error: null }),
      (e: unknown) => live && setState({ targets: [], error: errorText(e) }),
    );
    return () => {
      live = false;
    };
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sprint.id, sprint.projectId, sprint.applicationId]);
  return state;
}

/** Closing asks where the unfinished issues go: back to the backlog or into a planned sprint of the same scope. */
export function SprintClosePanel({ sprint, unfinished, onConfirm, onCancel }: {
  sprint: SprintSummary; unfinished: number; onConfirm: (moveTo: string | null) => Promise<void>; onCancel: () => void;
}) {
  const { t } = useI18n();
  const title = useSprintTitle();
  const { targets, error } = usePlannedTargets(sprint);
  const [target, setTarget] = useState("");
  const [busy, setBusy] = useState(false);

  const confirm = async () => {
    setBusy(true);
    try {
      await onConfirm(unfinished > 0 && target ? target : null);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t("sprints.closeTitle")}>
      <div className="stack">
        {error ? <Notice kind="error">{error}</Notice> : null}
        <p>{unfinished > 0 ? t("sprints.closeQuestion", { n: unfinished }) : t("sprints.closeNone")}</p>
        {unfinished > 0 ? (
          <Labeled label={t("sprints.moveTo")}>
            {(id) => (
              <select id={id} className="input" value={target} onChange={(e) => setTarget(e.target.value)}>
                <option value="">{t("sprints.toBacklog")}</option>
                {targets.map((s) => <option key={s.id} value={s.id}>{title(s)}</option>)}
              </select>
            )}
          </Labeled>
        ) : null}
        <div className="row-actions">
          <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
          <Button busy={busy} onClick={() => void confirm()}>{t("sprints.confirmClose")}</Button>
        </div>
      </div>
    </Card>
  );
}
