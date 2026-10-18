import { useState } from "react";
import { useI18n } from "../i18n";
import { time } from "../lib/format";
import { Markdown } from "../lib/markdown";
import type { ChatItem, Question, ToolResult } from "../lib/transcript";
import { noticeLine, fieldLabel } from "../lib/notice";
import { toolCounts, toolState, type ToolItem, type ToolRun } from "../lib/toolRuns";
import { config } from "../config";
import { useFold } from "./fold";

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

function ToolCall({ item }: { item: ToolItem }) {
  const { t } = useI18n();
  const state = toolState(item);
  const fold = useFold([item.key]);
  return (
    <details className={`chat-tool chat-tool-${state}`} {...fold}>
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
      {item.result?.shortened ? <p className="muted small chat-note">{t("chat.outputTooBig")}</p> : null}
    </details>
  );
}

/**
 * A run of tool calls closed to how many there were and of which tools, so a long stretch of work takes one line; open, it lists
 * each call (which opens to its input and output). Nothing is cut: names and summaries wrap.
 */
function ToolRunCard({ tools }: { tools: ToolItem[] }) {
  const { t } = useI18n();
  const states = tools.map(toolState);
  const failed = states.filter((s) => s === "error").length;
  const state = failed > 0 ? "error" : states.includes("pending") ? "pending" : "done";
  const fold = useFold(tools.map((tool) => `run:${tool.key}`)); // open while any of its calls is: it stays open as calls join it
  return (
    <details className={`chat-tool chat-tool-run chat-tool-${state}`} {...fold}>
      <summary>
        <span className="chat-tool-dot" aria-hidden />
        <strong className="chat-tool-name">{tools.length === 1 ? t("chat.toolRunOne") : t("chat.toolRun", { n: tools.length })}</strong>
        <span className="chat-tool-kinds">
          {toolCounts(tools).map((c) => <span key={c.name} className="chat-tool-kind">{c.name} ×{c.count}</span>)}
        </span>
        <span className="chat-tool-state">{failed > 0 ? t("chat.toolRunFailed", { n: failed }) : t(`chat.tool_${state}`)}</span>
      </summary>
      <div className="chat-tool-list">{tools.map((tool) => <ToolCall key={tool.key} item={tool} />)}</div>
    </details>
  );
}

/**
 * What Claude asked, closed to the question and the answer given; open, every option it offered, the one chosen marked. A question
 * not yet answered says so where the answer would be.
 */
function QuestionCard({ item }: { item: Extract<ChatItem, { kind: "question" }> }) {
  const { t } = useI18n();
  const answered = item.answers !== null;
  const chosen = (q: Question, label: string) => {
    const a = item.answers?.[q.question];
    return a !== undefined && (a === label || a.split(", ").includes(label));
  };
  const fold = useFold([item.key]);
  return (
    <details className="chat-question" {...fold}>
      <summary>
        {item.questions.map((q) => {
          const answer = item.answers?.[q.question];
          return (
            <span key={q.question} className="chat-q-line">
              {q.header ? <span className="badge">{q.header}</span> : null}
              <span className="chat-q-text">{q.question}</span>
              {answer !== undefined ? <span className="chat-answer">{t("chat.answer")}: {answer}</span> : null}
            </span>
          );
        })}
        {!answered ? <span className="muted small chat-q-line">{item.result ? item.result.text : t("chat.waitingAnswer")}</span> : null}
      </summary>
      {item.questions.map((q) => (
        <ul key={q.question} className="chat-options" aria-label={`${t("chat.options")}: ${q.question}`}>
          {q.options.map((o) => (
            <li key={o.label} className={chosen(q, o.label) ? "chat-option chat-option-chosen" : "chat-option"}>
              <span className="chat-option-label">{chosen(q, o.label) ? "✓ " : ""}{o.label}</span>
              {o.description ? <span className="muted small">{o.description}</span> : null}
            </li>
          ))}
        </ul>
      ))}
    </details>
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

/** Sends an answer to the session as a message from the web; the card shows what was sent until the session's own line replaces it. */
export type Reply = (text: string) => Promise<void>;

/**
 * The desktop app's "next step" box, which only the app's own window can answer: the question with its buttons, here
 * answerable by whoever may command the session. An answer is a message from the web, the same as a click in the app sends.
 */
function NextStepCard({ item, reply }: { item: Extract<ChatItem, { kind: "nextstep" }>; reply: Reply | null }) {
  const { t } = useI18n();
  const [text, setText] = useState("");
  const [sent, setSent] = useState<string | null>(null);
  const [error, setError] = useState(false);
  const open = !item.answered && sent === null && reply !== null;
  const send = async (message: string) => {
    if (!reply || !message.trim()) return;
    setError(false);
    try {
      await reply(message.trim());
      setSent(message.trim());
    } catch {
      setError(true);
    }
  };
  return (
    <div className="chat-nextstep">
      {item.recap ? <div className="muted small"><Markdown text={item.recap} /></div> : null}
      <div className="chat-q-text"><Markdown text={item.ask} /></div>
      {open ? (
        <>
          <div className="prompt-options" role="group" aria-label={item.ask}>
            {item.actions.map((a) => (
              <button key={a.label} type="button" className="prompt-option" title={a.message} onClick={() => void send(a.message)}>
                <span className="chat-option-label">{a.label}</span>
              </button>
            ))}
          </div>
          <form className="chat-reply" onSubmit={(e) => { e.preventDefault(); void send(text); }}>
            <input className="input" placeholder={t("chat.replyPlaceholder")} maxLength={config.answerMax} value={text} onChange={(e) => setText(e.target.value)} />
            <button type="submit" className="btn" disabled={!text.trim()}>{t("chat.replySend")}</button>
          </form>
          {error ? <p className="error" role="alert">{t("chat.replyFailed")}</p> : null}
        </>
      ) : null}
      {sent !== null && !item.answered ? <p className="muted small">{t("chat.replySent", { text: sent })}</p> : null}
      {item.answered ? <p className="muted small">{t("chat.nextStepAnswered")}</p> : null}
      {!item.answered && sent === null && reply === null ? <p className="muted small">{t("chat.nextStepWaiting")}</p> : null}
    </div>
  );
}

/** Text the harness wrote into the session (a finished background task, ...): one closed line, and opened the fields as plain text. */
function NoticeCard({ item }: { item: Extract<ChatItem, { kind: "notice" }> }) {
  const fold = useFold([item.key]);
  const line = noticeLine(item.notice);
  return (
    <details className="chat-notice" {...fold}>
      <summary>
        <span className="badge">{line.label}</span>
        {line.status ? <span className="chat-notice-status">{line.status}</span> : null}
        <span className="chat-notice-summary">{line.summary}</span>
      </summary>
      <dl className="chat-notice-fields">
        {item.notice.fields.map((f, i) => <div key={`${f.name}:${i}`}><dt>{fieldLabel(f.name)}</dt><dd>{f.value}</dd></div>)}
      </dl>
    </details>
  );
}

function Thinking({ item }: { item: Extract<ChatItem, { kind: "thinking" }> }) {
  const { t } = useI18n();
  const fold = useFold([item.key]);
  return <details {...fold}><summary>{t("chat.thinking")}</summary><Markdown text={item.text} /></details>;
}

/** A run of tool calls as one line of the conversation. */
export function ToolRunLine({ run }: { run: ToolRun }) {
  return <li className="chat-row" data-kind="toolrun"><ToolRunCard tools={run.tools} /></li>;
}

export function ChatLine({ item, reply = null }: { item: ChatItem; reply?: Reply | null }) {
  const { t } = useI18n();
  switch (item.kind) {
    case "user":
      return (
        <li className="chat-row chat-user" data-kind="user">
          <div className="chat-bubble">
            {item.command ? <code className="chat-command-name">{item.command}</code> : null}
            {item.text ? <Markdown text={item.text} /> : null}
            <time className="chat-time" dateTime={item.at}>{time(item.at)}</time>
          </div>
        </li>
      );
    case "notice":
      return <li className="chat-row" data-kind="notice"><NoticeCard item={item} /></li>;
    case "assistant":
      return <li className="chat-row chat-assistant" data-kind="assistant"><Markdown text={item.text} /></li>;
    case "thinking":
      return <li className="chat-row chat-thinking" data-kind="thinking"><Thinking item={item} /></li>;
    case "tool":
      return <li className="chat-row" data-kind="tool"><ToolCall item={item} /></li>;
    case "question":
      return <li className="chat-row" data-kind="question"><QuestionCard item={item} /></li>;
    case "plan":
      return <li className="chat-row" data-kind="plan"><PlanCard item={item} /></li>;
    case "nextstep":
      return <li className="chat-row" data-kind="nextstep"><NextStepCard item={item} reply={reply} /></li>;
    case "shortened":
      return <li className="chat-row chat-shortened" data-kind="shortened"><span className="badge">{t("session.truncated")}</span> <code>{item.text.slice(0, 200)}</code></li>;
    case "note":
      return (
        <li className={`chat-row chat-note chat-note-${item.tone}`} data-kind="note">
          <time dateTime={item.at}>{time(item.at)}</time> <span className="chat-note-tone">{t(`chat.note_${item.tone}`)}</span> {item.text}
        </li>
      );
    default:
      return null;
  }
}
