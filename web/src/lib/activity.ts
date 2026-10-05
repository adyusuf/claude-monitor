import type { EventRow } from "../api/types";

// What the Activity list knows about the events the agent reports (src/ClaudeMonitor.Contracts/Codes.cs EventKinds and
// Claude Code's own hook and transcript field names). One place, so the list never matches on a bare string (global #11).

export const EventKind = {
  Prompt: "hook:UserPromptSubmit",
  PreTool: "hook:PreToolUse",
  PostTool: "hook:PostToolUse",
  PostToolFailure: "hook:PostToolUseFailure",
  PermissionRequest: "hook:PermissionRequest",
  Notification: "hook:Notification",
  SubagentStart: "hook:SubagentStart",
  SubagentStop: "hook:SubagentStop",
  Transcript: "transcript",
  Usage: "usage",
  Note: "note",
} as const;

export const TranscriptType = { User: "user", Assistant: "assistant", Attachment: "attachment" } as const;
export const ContentType = { Text: "text", ToolUse: "tool_use", ToolResult: "tool_result" } as const;
export const CommandKind = { Prompt: "prompt", Stop: "stop" } as const;
export const CommandStatus = { Queued: "queued", Delivered: "delivered", Applied: "applied" } as const;

/** What a line of the Activity list is, for how it looks and how the filter treats it. */
export const ActivityClass = {
  MonitorInput: "monitor_input",
  HumanInput: "human_input",
  Assistant: "assistant",
  Tool: "tool",
  Meta: "meta",
} as const;
export type ActivityClassName = (typeof ActivityClass)[keyof typeof ActivityClass];

interface Block { type?: string; text?: string; name?: string; input?: Record<string, unknown> }

/** The content blocks of a transcript message: a plain string is one text block. */
function blocks(p: Record<string, unknown>): Block[] {
  const content = (p.message as { content?: unknown } | undefined)?.content;
  if (typeof content === "string") return content ? [{ type: ContentType.Text, text: content }] : [];
  return Array.isArray(content) ? content.filter((c): c is Block => typeof c === "object" && c !== null) : [];
}

const str = (p: Record<string, unknown>, key: string) => (typeof p[key] === "string" ? (p[key] as string) : "");
const toolDetail = (input: Record<string, unknown> | undefined) =>
  input ? String(input.command ?? input.file_path ?? input.description ?? input.pattern ?? "") : "";

/** The text a person typed, from a transcript "user" line; "" when the line is a tool result, a meta line or has no text. */
export function transcriptUserText(e: EventRow): string {
  const p = e.payload;
  if (e.kind !== EventKind.Transcript || str(p, "type") !== TranscriptType.User || p.isMeta === true) return "";
  const parts = blocks(p);
  if (parts.some((b) => b.type === ContentType.ToolResult)) return "";
  return parts.filter((b) => b.type === ContentType.Text).map((b) => b.text ?? "").join("\n").trim();
}

/** Which class an event belongs to. A transcript user line with text is a HUMAN input candidate: the timeline decides whether it only repeats a hook. */
export function classifyEvent(e: EventRow): ActivityClassName {
  switch (e.kind) {
    case EventKind.Prompt:
      return ActivityClass.HumanInput;
    case EventKind.PreTool:
    case EventKind.PostTool:
    case EventKind.PostToolFailure:
    case EventKind.PermissionRequest:
      return ActivityClass.Tool;
    case EventKind.Transcript:
      return classifyTranscript(e);
    default:
      // usage, notifications, subagents, notes and any kind a newer agent adds
      return ActivityClass.Meta;
  }
}

function classifyTranscript(e: EventRow): ActivityClassName {
  const p = e.payload;
  const parts = blocks(p);
  switch (str(p, "type")) {
    case TranscriptType.Assistant:
      if (parts.some((b) => b.type === ContentType.Text && b.text)) return ActivityClass.Assistant;
      return parts.some((b) => b.type === ContentType.ToolUse) ? ActivityClass.Tool : ActivityClass.Meta;
    case TranscriptType.User:
      if (parts.some((b) => b.type === ContentType.ToolResult)) return ActivityClass.Tool;
      return transcriptUserText(e) ? ActivityClass.HumanInput : ActivityClass.Meta;
    default:
      return ActivityClass.Meta;
  }
}

/** A one-line summary of an event, from the fields Claude Code's hooks carry. */
export function summary(e: EventRow): string {
  const p = e.payload;
  const input = p.tool_input as Record<string, unknown> | undefined;
  switch (e.kind) {
    case EventKind.Prompt:
      return str(p, "prompt");
    case EventKind.PreTool:
    case EventKind.PostTool:
    case EventKind.PostToolFailure:
    case EventKind.PermissionRequest:
      return [str(p, "tool_name"), toolDetail(input)].filter(Boolean).join(" · ");
    case EventKind.Notification:
      return str(p, "message");
    case EventKind.SubagentStart:
    case EventKind.SubagentStop:
      return str(p, "agent_type");
    case EventKind.Note:
      return str(p, "text");
    case EventKind.Transcript: {
      const parts = blocks(p);
      const text = parts.find((b) => b.type === ContentType.Text && b.text)?.text;
      const use = parts.find((b) => b.type === ContentType.ToolUse);
      return text ?? (use ? [use.name, toolDetail(use.input)].filter(Boolean).join(" · ") : str(p, "type"));
    }
    default:
      return "";
  }
}

/** The kind as the list names it: "hook:PreToolUse" is "PreToolUse". */
export const kindLabel = (kind: string) => kind.replace(/^hook:/, "");
