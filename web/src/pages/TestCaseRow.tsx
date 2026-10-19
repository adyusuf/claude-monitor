import { useState } from "react";
import { api } from "../api/endpoints";
import type { TestCaseView, TestResult } from "../api/typesIssues";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { issueLimits, TEST_RESULTS } from "../lib/issueOptions";
import { TestCaseForm } from "./TestCaseForm";

/** One scenario with its latest result: who set it and when, the note, and (for those who may) the buttons that set a result, edit or delete it. */
export function TestCaseRow({ testCase: tc, canWrite, act }: { testCase: TestCaseView; canWrite: boolean; act: (work: () => Promise<unknown>, done?: () => void) => Promise<boolean> }) {
  const { t } = useI18n();
  const [editing, setEditing] = useState(false);
  const [note, setNote] = useState(tc.resultNote ?? "");
  const tone = tc.result === "passed" ? " ok" : tc.result === "failed" || tc.result === "blocked" ? " warn" : "";
  const setResult = (result: TestResult) => void act(() => api.setTestResult(tc.id, result, note.trim() === "" ? null : note.trim()));

  if (editing) {
    return (
      <li>
        <TestCaseForm initial={tc} submitLabel={t("common.save")} onCancel={() => setEditing(false)}
          onSubmit={(input) => act(() => api.putTestCase(tc.id, input), () => setEditing(false))} />
      </li>
    );
  }
  return (
    <li className="test-case">
      <div className="spread">
        <strong>{tc.title}</strong>
        <span className={`badge${tone}`}>{t(`issues.results.${tc.result}`)}</span>
      </div>
      {tc.steps ? <p className="pre"><span className="muted">{t("issues.testSteps")}: </span>{tc.steps}</p> : null}
      {tc.expected ? <p className="pre"><span className="muted">{t("issues.testExpected")}: </span>{tc.expected}</p> : null}
      {tc.resultAt ? (
        <p className="muted small">{t("issues.resultBy", { name: tc.resultByName ?? "–", when: dateTime(tc.resultAt) })}{tc.resultNote ? <> · <span className="pre">{tc.resultNote}</span></> : null}</p>
      ) : null}
      {canWrite ? (
        <div className="stack">
          <input className="input" aria-label={`${t("issues.resultNote")} ${tc.title}`} placeholder={t("issues.resultNote")} maxLength={issueLimits.noteMax}
            value={note} onChange={(e) => setNote(e.target.value)} />
          <div className="row-actions">
            {TEST_RESULTS.map((r) => (
              <Button key={r} variant={tc.result === r ? "primary" : "ghost"} aria-label={`${t(`issues.results.${r}`)}: ${tc.title}`} onClick={() => setResult(r)}>{t(`issues.results.${r}`)}</Button>
            ))}
            <Button variant="ghost" aria-label={`${t("projects.edit")}: ${tc.title}`} onClick={() => setEditing(true)}>{t("projects.edit")}</Button>
            <Button variant="ghost" aria-label={`${t("issues.deleteTest")}: ${tc.title}`}
              onClick={() => window.confirm(t("issues.deleteTestConfirm", { title: tc.title })) && void act(() => api.deleteTestCase(tc.id))}>{t("issues.deleteTest")}</Button>
          </div>
        </div>
      ) : null}
    </li>
  );
}
