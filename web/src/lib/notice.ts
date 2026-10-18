// Text the harness writes into a session as a "user" line is tagged, not speech: a finished background task is
//   <task-notification><task-id>…</task-id><status>completed</status><summary>…</summary></task-notification>
// The chat shows it as a short closed line and, opened, the fields as plain text instead of the tags.

export interface NoticeField { name: string; value: string }
export interface Notice { tag: string; fields: NoticeField[] }

const OPENING = /^\s*<([a-z][a-z0-9_-]*)[\s>]/;
const ELEMENT = /<([a-z][a-z0-9_-]*)>([\s\S]*?)<\/\1>/g;
const ENTITIES: Record<string, string> = { "&lt;": "<", "&gt;": ">", "&quot;": "\"", "&apos;": "'", "&amp;": "&" };

const plain = (s: string): string => s.replace(/&(?:lt|gt|quot|apos|amp);/g, (m) => ENTITIES[m] ?? m).trim();

/** The elements at the top of a text, and the text between and around them. */
function elements(text: string): { found: NoticeField[]; rest: string } {
  const found = [...text.matchAll(ELEMENT)].map((m) => ({ name: m[1]!, value: m[2]! }));
  return { found, rest: text.replace(ELEMENT, " ").trim() };
}

/** "task-id" is "Task id". */
export const fieldLabel = (name: string): string => {
  const words = name.replace(/[-_]+/g, " ").trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
};

/**
 * The tagged text as a title (its tag) and fields, or null when the text is not tagged. Nothing is dropped: text that is not in a
 * known shape (no closing tag, text around the elements) is kept as a field named "text".
 */
export function parseNotice(raw: string): Notice | null {
  const text = raw.trim();
  const tag = OPENING.exec(text)?.[1];
  if (!tag) return null;
  const top = elements(text);
  let fields: NoticeField[];
  if (top.found.length === 1 && top.rest === "") {
    const inner = elements(top.found[0]!.value);
    fields = inner.found.length > 0 ? [...inner.found, ...(inner.rest ? [{ name: "text", value: inner.rest }] : [])] : [{ name: "text", value: top.found[0]!.value }];
  } else if (top.found.length > 0) {
    fields = [...top.found, ...(top.rest ? [{ name: "text", value: top.rest }] : [])];
  } else {
    fields = [{ name: "text", value: text }];
  }
  return { tag, fields: fields.map((f) => ({ name: f.name, value: plain(f.value) })).filter((f) => f.value !== "") };
}

const first = (s: string) => s.split("\n", 1)[0]!.trim();

/** What the closed line says: the tag, the status when there is one, and the one line that sums it up. */
export function noticeLine(n: Notice): { label: string; status: string; summary: string } {
  const field = (name: string) => n.fields.find((f) => f.name === name)?.value ?? "";
  const lead = field("summary") || field("message") || field("text") || n.fields.find((f) => !/(^|-)(id|file|path)$/.test(f.name))?.value || n.fields[0]?.value || "";
  return { label: fieldLabel(n.tag), status: field("status"), summary: first(lead) };
}
