import { useState, type FormEvent } from "react";
import type { TestCaseInput, TestCaseView } from "../api/typesIssues";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { issueLimits } from "../lib/issueOptions";

const text = (value: string): string | null => value.trim() === "" ? null : value.trim();

/** A test scenario: title, steps and the expected result. Shared by "add" and "edit"; a blank title is refused before the API is asked, and an added scenario clears the form only once it was saved. */
export function TestCaseForm({ initial, submitLabel, onSubmit, onCancel }: {
  initial?: TestCaseView; submitLabel: string; onSubmit: (input: TestCaseInput) => Promise<boolean>; onCancel?: () => void;
}) {
  const { t } = useI18n();
  const [title, setTitle] = useState(initial?.title ?? "");
  const [steps, setSteps] = useState(initial?.steps ?? "");
  const [expected, setExpected] = useState(initial?.expected ?? "");
  const [missing, setMissing] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (title.trim() === "") return setMissing(true);
    setMissing(false);
    const saved = await onSubmit({ title: title.trim(), steps: text(steps), expected: text(expected) });
    if (saved && !initial) {
      setTitle("");
      setSteps("");
      setExpected("");
    }
  };

  const label = initial ? ` ${initial.title}` : "";
  return (
    <form className="stack test-form" onSubmit={(e) => void submit(e)}>
      <input className={missing ? "input input-error" : "input"} aria-label={`${t("issues.testTitle")}${label}`} placeholder={t("issues.testTitle")}
        maxLength={issueLimits.titleMax} value={title} onChange={(e) => setTitle(e.target.value)} />
      {missing ? <span className="field-error" role="alert">{t("errors.invalid_name")}</span> : null}
      <textarea className="input" rows={2} aria-label={`${t("issues.testSteps")}${label}`} placeholder={t("issues.testSteps")} maxLength={issueLimits.stepsMax}
        value={steps} onChange={(e) => setSteps(e.target.value)} />
      <textarea className="input" rows={2} aria-label={`${t("issues.testExpected")}${label}`} placeholder={t("issues.testExpected")} maxLength={issueLimits.resultMax}
        value={expected} onChange={(e) => setExpected(e.target.value)} />
      <div className="row-actions">
        {onCancel ? <Button variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button> : null}
        <Button type="submit">{submitLabel}</Button>
      </div>
    </form>
  );
}
