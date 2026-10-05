import { useState } from "react";
import { Button } from "../components/ui";
import { config } from "../config";
import { useI18n } from "../i18n";
import { Markdown } from "../lib/markdown";
import { questionsOf, type Question } from "../lib/transcript";

/** The tool names whose permission prompt is a question or a plan, as the harness calls them. */
export const QUESTION_TOOL = "AskUserQuestion";
export const PLAN_TOOL = "ExitPlanMode";

/**
 * Claude's question with its options as buttons: clicking one answers it in the session. One single-choice question is
 * answered by the click itself; otherwise the choices are collected and sent together. The answer is only ever one of
 * the options (or short typed text); the agent puts it into the question the session itself asked.
 */
export function QuestionPrompt({ input, onAnswer }: { input: Record<string, unknown>; onAnswer: (answers: Record<string, string>) => void }) {
  const { t } = useI18n();
  const questions = questionsOf(input);
  const [picked, setPicked] = useState<Record<string, string[]>>({});
  const [other, setOther] = useState<Record<string, string>>({});
  const direct = questions.length === 1 && !questions[0]!.multiSelect;

  const chosen = (q: Question) => [...(picked[q.question] ?? []), ...(other[q.question]?.trim() ? [other[q.question]!.trim()] : [])];
  const ready = questions.length > 0 && questions.every((q) => chosen(q).length > 0);
  const toggle = (q: Question, label: string) => setPicked((p) => {
    const now = p[q.question] ?? [];
    return { ...p, [q.question]: q.multiSelect ? (now.includes(label) ? now.filter((l) => l !== label) : [...now, label]) : [label] };
  });
  const send = () => onAnswer(Object.fromEntries(questions.map((q) => [q.question, chosen(q).join(", ")])));

  return (
    <div className="prompt-question">
      {questions.map((q) => (
        <div key={q.question} className="chat-q">
          {q.header ? <span className="badge">{q.header}</span> : null}
          <p className="chat-q-text">{q.question}</p>
          <div className="prompt-options" role="group" aria-label={q.question}>
            {q.options.map((o) => (
              <button key={o.label} type="button" className={picked[q.question]?.includes(o.label) ? "prompt-option prompt-option-on" : "prompt-option"}
                aria-pressed={picked[q.question]?.includes(o.label) ?? false}
                onClick={() => (direct ? onAnswer({ [q.question]: o.label }) : toggle(q, o.label))}>
                <span className="chat-option-label">{o.label}</span>
                {o.description ? <span className="muted small">{o.description}</span> : null}
              </button>
            ))}
          </div>
          {!direct ? (
            <input className="input" placeholder={t("chat.otherAnswer")} maxLength={config.answerMax} value={other[q.question] ?? ""}
              onChange={(e) => setOther({ ...other, [q.question]: e.target.value })} />
          ) : null}
        </div>
      ))}
      {!direct ? <div className="permission-actions"><Button disabled={!ready} onClick={send}>{t("chat.sendAnswers")}</Button></div> : null}
    </div>
  );
}

/** A plan waiting for approval: the plan itself, to approve here or to send back with a note. */
export function PlanPrompt({ input }: { input: Record<string, unknown> }) {
  const { t } = useI18n();
  return (
    <div className="prompt-plan">
      <strong>{t("chat.plan")}</strong>
      <Markdown text={typeof input.plan === "string" ? input.plan : ""} />
    </div>
  );
}
