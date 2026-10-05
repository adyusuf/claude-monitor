// Turns the session's "transcript" events (Claude Code's JSONL lines, one event each) into what its screen shows:
// what was asked, what Claude said, what it ran and what came back, the questions it put with their options.
// Pure functions, no React: the page feeds them the events it has loaded, oldest first.
import type { EventRow } from "../api/types";

export interface QuestionOption { label: string; description: string }
export interface Question { question: string; header: string; multiSelect: boolean; options: QuestionOption[] }
/** A tool's output; `shortened` is a result the agent cut for size, so only the fact that it came back is known. */
export interface ToolResult { text: string; isError: boolean; shortened?: boolean }

interface Base { key: string; at: string }
export type ChatItem =
  | (Base & { kind: "user"; text: string; command: string | null })
  | (Base & { kind: "assistant"; text: string })
  | (Base & { kind: "thinking"; text: string })
  | (Base & { kind: "tool"; id: string; name: string; summary: string; input: Record<string, unknown>; result: ToolResult | null; settled: boolean })
  | (Base & { kind: "question"; id: string; questions: Question[]; answers: Record<string, string> | null; result: ToolResult | null })
  | (Base & { kind: "plan"; id: string; plan: string; result: ToolResult | null })
  | (Base & { kind: "shortened"; text: string })
  | (Base & { kind: "note"; tone: "notification" | "subagent_start" | "subagent_stop"; text: string });

type Obj = Record<string, unknown>;
type Found = ToolResult & { answers: Record<string, string> | null };
const isObj = (v: unknown): v is Obj => typeof v === "object" && v !== null && !Array.isArray(v);
const str = (v: unknown): string => (typeof v === "string" ? v : "");

/** The text of a tool result: a string, or a list of text blocks. */
function resultText(content: unknown): string {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.map((b) => (isObj(b) ? str(b.text) : "")).filter(Boolean).join("\n");
}

/** A one-line description of a tool call, from the field each tool is known by. */
export function toolSummary(name: string, input: Obj): string {
  const pick = (...keys: string[]) => keys.map((k) => str(input[k])).find(Boolean) ?? "";
  switch (name) {
    case "Bash":
      return pick("command");
    case "Read":
    case "Edit":
    case "Write":
    case "NotebookEdit":
      return pick("file_path", "notebook_path");
    case "Grep":
    case "Glob":
      return pick("pattern");
    case "WebFetch":
      return pick("url");
    case "WebSearch":
      return pick("query");
    case "Task":
    case "Agent":
      return pick("description", "prompt");
    case "TodoWrite":
      return Array.isArray(input.todos) ? `${input.todos.length}` : "";
    default:
      return pick("command", "file_path", "description", "pattern", "query", "url", "prompt");
  }
}

function questionsOf(input: Obj): Question[] {
  const list = Array.isArray(input.questions) ? input.questions : [];
  return list.filter(isObj).map((q) => ({
    question: str(q.question),
    header: str(q.header),
    multiSelect: q.multiSelect === true,
    options: (Array.isArray(q.options) ? q.options : []).filter(isObj).map((o) => ({ label: str(o.label), description: str(o.description) })),
  }));
}

/** The call a cut-down line answers, when its preview still carries the call's id. */
function cutResultOf(e: EventRow): string | undefined {
  if (e.payload.truncated !== true) return undefined;
  const preview = str(e.payload.preview);
  return /"type":"tool_result"/.test(preview) ? /"tool_use_id":"([^"]+)"/.exec(preview)?.[1] : undefined;
}

/** Hook events the conversation shows besides the transcript: Claude asking for the user, and subagents coming and going. */
function noteOf(e: EventRow): { tone: "notification" | "subagent_start" | "subagent_stop"; text: string } | null {
  switch (e.kind) {
    case "hook:Notification":
      return { tone: "notification", text: str(e.payload.message) };
    case "hook:SubagentStart":
      return { tone: "subagent_start", text: str(e.payload.agent_type) };
    case "hook:SubagentStop":
      return { tone: "subagent_stop", text: str(e.payload.agent_type) };
    default:
      return null;
  }
}

const WEB_HEADER = "Messages sent to this session from Claude Monitor (the web):";

/** A message sent from the web while the user typed one reaches Claude as hook context; the transcript keeps it as an attachment. */
function fromWeb(p: Obj): string | null {
  const a = p.type === "attachment" && isObj(p.attachment) ? p.attachment : null;
  if (!a || !str(a.type).includes("hook_additional_context")) return null;
  const text = (Array.isArray(a.content) ? a.content.map(str).join("\n") : str(a.content)).trim();
  const at = text.indexOf(WEB_HEADER);
  if (at < 0) return null;
  return text.slice(at + WEB_HEADER.length).split("\n").map((l) => l.replace(/^\s*-\s?/, "")).join("\n").trim() || null;
}

/** What a user line says. A slash command arrives wrapped in tags; the command's own output and reminders are not speech. */
function userText(raw: string): { text: string; command: string | null } | null {
  const text = raw.trim();
  if (!text || text.startsWith("<local-command-") || text.startsWith("<system-reminder>") || text.startsWith("Caveat:")) return null;
  const name = /<command-name>([^<]*)<\/command-name>/.exec(text)?.[1];
  if (name) {
    const args = /<command-args>([^<]*)<\/command-args>/.exec(text)?.[1] ?? "";
    return { text: args.trim(), command: name.trim() };
  }
  return { text, command: null };
}

/**
 * The chat for a run of events (oldest first). A tool call is joined with its result wherever the result is in the
 * run; a result whose call is not loaded (it is on an older page) shows nothing until that page arrives.
 */
export function buildChat(events: EventRow[]): ChatItem[] {
  const results = new Map<string, Found>();
  for (const e of events) {
    // The agent keeps only a preview of a line over the size cap. If the preview still names the call it answers, that
    // call is settled (its output is just too big to show); otherwise the line is shown below as a shortened note.
    const answered = cutResultOf(e);
    if (answered) results.set(answered, { text: "", isError: false, answers: null, shortened: true });
    const content = isObj(e.payload.message) ? e.payload.message.content : null;
    if (!Array.isArray(content)) continue;
    const extra = isObj(e.payload.toolUseResult) ? e.payload.toolUseResult : null;
    for (const b of content) {
      if (!isObj(b) || b.type !== "tool_result") continue;
      const answers = extra && isObj(extra.answers) ? Object.fromEntries(Object.entries(extra.answers).map(([k, v]) => [k, str(v)])) : null;
      results.set(str(b.tool_use_id), { text: resultText(b.content), isError: b.is_error === true, answers });
    }
  }

  const items: ChatItem[] = [];
  for (const e of events) {
    const p = e.payload;
    const at = e.occurredAt;
    if (p.truncated === true) {
      if (!cutResultOf(e)) items.push({ kind: "shortened", key: `${e.id}`, at, text: str(p.preview) });
      continue;
    }

    const note = noteOf(e);
    if (note) {
      items.push({ kind: "note", key: `${e.id}`, at, ...note });
      continue;
    }

    const web = fromWeb(p);
    if (web) {
      items.push({ kind: "user", key: `${e.id}`, at, text: web, command: null });
      continue;
    }

    if (p.isSidechain === true || p.isMeta === true || !isObj(p.message)) continue;
    const content = p.message.content;
    if (p.type === "user") {
      const texts = typeof content === "string" ? [content] : Array.isArray(content) ? content.filter(isObj).filter((b) => b.type === "text").map((b) => str(b.text)) : [];
      texts.forEach((raw, i) => {
        const said = userText(raw);
        if (said) items.push({ kind: "user", key: `${e.id}:${i}`, at, ...said });
      });
    } else if (p.type === "assistant" && Array.isArray(content)) {
      content.filter(isObj).forEach((b, i) => {
        const key = `${e.id}:${i}`;
        if (b.type === "text" && str(b.text).trim()) items.push({ kind: "assistant", key, at, text: str(b.text) });
        else if (b.type === "thinking" && str(b.thinking).trim()) items.push({ kind: "thinking", key, at, text: str(b.thinking) });
        else if (b.type === "tool_use") items.push(toolItem(key, at, str(b.id), str(b.name), isObj(b.input) ? b.input : {}, results.get(str(b.id))));
      });
    }
  }

  // A call with no result is running only while nothing came after it but other calls (parallel calls answer together);
  // anything later, a message, a thought, a question, a plan, means it ended even if its result is not among the lines read.
  let spoken = false;
  for (let i = items.length - 1; i >= 0; i--) {
    const item = items[i]!;
    if (item.kind === "tool") item.settled = spoken;
    else if (item.kind !== "note") spoken = true; // a note (the user is wanted, a subagent started) says the call is still going
  }

  return items;
}

function toolItem(key: string, at: string, id: string, name: string, input: Obj, found: Found | undefined): ChatItem {
  const result = found ? { text: found.text, isError: found.isError, ...(found.shortened ? { shortened: true } : {}) } : null;
  if (name === "AskUserQuestion") return { kind: "question", key, at, id, questions: questionsOf(input), answers: found?.answers ?? null, result };
  if (name === "ExitPlanMode") return { kind: "plan", key, at, id, plan: str(input.plan), result };
  return { kind: "tool", key, at, id, name, summary: toolSummary(name, input), input, result, settled: false };
}
