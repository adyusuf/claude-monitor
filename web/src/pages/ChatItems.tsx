import { useState } from "react";
import { useI18n } from "../i18n";
import { time } from "../lib/format";
import { Markdown } from "../lib/markdown";
import type { ChatItem, Question, ToolResult } from "../lib/transcript";
import { config } from "../config";

/** Output longer than this is cut on the page (the event itself is whole); the rest opens with "Show all". */
function Output({ result }: { result: ToolResult }) {
  const { t } = useI18n();
  const [all, setAll] = useState(false);
  const long = result.text.length > config.chatOutputChars;
  return (
    <div className={result.isError ? "chat-output chat-output-error" : "chat-output"}>
      <pre>{all || !long ? result.text : `${result.text.slice(0, config.chatOutputChars)}…`}</pre>
      {long ? <button type="button" className="link" onClick={() => setAll(!all)}>{all ? t("chat.showLess") : t("chat.showAll")}</button> : null}
    </div>
  );
}

function ToolCall({ item }: { item: Extract<ChatItem, { kind: "tool" }> }) {
  const { t } = useI18n();
  const state = item.result === null ? "pending" : item.result.isError ? "error" : "done";
  return (
    <details className={`chat-tool chat-tool-${state}`}>
      <summary>
        <span className="chat-tool-dot" aria-hidden />
        <strong className="chat-tool-name">{item.name}</strong>
        <span className="chat-tool-summary">{item.summary}</span>
        <span className="chat-tool-state">{t(`chat.tool_${state}`)}</span>
      </summary>
      {item.name === "Bash" && typeof item.input.command === "string"
        ? <pre className="chat-command">$ {item.input.command}</pre>
        : <pre className="chat-input">{JSON.stringify(item.input, null, 2)}</pre>}
      {item.result?.text ? <Output result={item.result} /> : null}
    </details>
  );
}

/** What Claude asked, with the options it offered; the one chosen is marked once the question was answered. */
function QuestionCard({ item }: { item: Extract<ChatItem, { kind: "question" }> }) {
  const { t } = useI18n();
  const answered = item.answers !== null;
  const chosen = (q: Question, label: string) => {
    const a = item.answers?.[q.question];
    return a !== undefined && (a === label || a.split(", ").includes(label));
  };
  return (
    <div className="chat-question">
      {item.questions.map((q) => (
        <div key={q.question} className="chat-q">
          {q.header ? <span className="badge">{q.header}</span> : null}
          <p className="chat-q-text">{q.question}</p>
          <ul className="chat-options" aria-label={t("chat.options")}>
            {q.options.map((o) => (
              <li key={o.label} className={chosen(q, o.label) ? "chat-option chat-option-chosen" : "chat-option"}>
                <span className="chat-option-label">{chosen(q, o.label) ? "✓ " : ""}{o.label}</span>
                {o.description ? <span className="muted small">{o.description}</span> : null}
              </li>
            ))}
          </ul>
          {answered && item.answers![q.question] !== undefined && !q.options.some((o) => chosen(q, o.label))
            ? <p className="chat-answer">{t("chat.answer")}: {item.answers![q.question]}</p> : null}
        </div>
      ))}
      {!answered ? <p className="muted small">{item.result ? item.result.text : t("chat.waitingAnswer")}</p> : null}
    </div>
  );
}

function PlanCard({ item }: { item: Extract<ChatItem, { kind: "plan" }> }) {
  const { t } = useI18n();
  return (
    <div className="chat-plan">
      <strong>{t("chat.plan")}</strong>
      <Markdown text={item.plan} />
      <p className="muted small">{item.result ? item.result.text.split("\n")[0] : t("chat.waitingApproval")}</p>
    </div>
  );
}

export function ChatLine({ item }: { item: ChatItem }) {
  const { t } = useI18n();
  switch (item.kind) {
    case "user":
      return (
        <li className="chat-row chat-user" data-kind="user">
          <div className="chat-bubble">
            {item.command ? <code className="chat-command-name">{item.command}</code> : null}
            {item.text ? <p>{item.text}</p> : null}
            <time className="chat-time" dateTime={item.at}>{time(item.at)}</time>
          </div>
        </li>
      );
    case "assistant":
      return <li className="chat-row chat-assistant" data-kind="assistant"><Markdown text={item.text} /></li>;
    case "thinking":
      return (
        <li className="chat-row chat-thinking" data-kind="thinking">
          <details><summary>{t("chat.thinking")}</summary><p>{item.text}</p></details>
        </li>
      );
    case "tool":
      return <li className="chat-row" data-kind="tool"><ToolCall item={item} /></li>;
    case "question":
      return <li className="chat-row" data-kind="question"><QuestionCard item={item} /></li>;
    case "plan":
      return <li className="chat-row" data-kind="plan"><PlanCard item={item} /></li>;
    case "shortened":
      return <li className="chat-row chat-shortened" data-kind="shortened"><span className="badge">{t("session.truncated")}</span> <code>{item.text.slice(0, 200)}</code></li>;
    default:
      return null;
  }
}
