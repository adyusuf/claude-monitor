import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { TestCaseView } from "../api/typesIssues";
import { Card, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { TestCaseForm } from "./TestCaseForm";
import { TestCaseRow } from "./TestCaseRow";

/** The test scenarios of an issue and the latest result of each; those who may change the issue add, edit, delete and set results. */
export function IssueTestCases({ issueId, canWrite }: { issueId: string; canWrite: boolean }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [rows, setRows] = useState<TestCaseView[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRows(await api.testCases(issueId));
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render; loading again for it would loop
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [issueId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (work: () => Promise<unknown>, done?: () => void): Promise<boolean> => {
    try {
      setError(null);
      await work();
      done?.();
      await load();
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    }
  };

  return (
    <Card title={t("issues.testCases")}>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {rows === null ? <Spinner /> : rows.length === 0 ? <p className="muted">{t("issues.noTests")}</p> : (
        <ul className="plain">
          {rows.map((tc) => <TestCaseRow key={tc.id} testCase={tc} canWrite={canWrite} act={act} />)}
        </ul>
      )}
      {canWrite ? (
        <div className="add-row">
          <TestCaseForm submitLabel={t("issues.addTest")} onSubmit={(input) => act(() => api.addTestCase(issueId, input))} />
        </div>
      ) : null}
    </Card>
  );
}
