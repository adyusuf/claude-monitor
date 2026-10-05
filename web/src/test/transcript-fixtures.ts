import type { EventRow } from "../api/types";

export const at = "2026-10-05T10:00:00Z";
export const ev = (id: number, payload: Record<string, unknown>): EventRow => ({ id, kind: "transcript", occurredAt: at, truncated: false, payload });
export const user = (id: number, text: string) => ev(id, { type: "user", message: { role: "user", content: text } });
export const said = (id: number, text: string) => ev(id, { type: "assistant", message: { role: "assistant", content: [{ type: "text", text }] } });
export const call = (id: number, toolId: string, name: string, input: Record<string, unknown>) =>
  ev(id, { type: "assistant", message: { role: "assistant", content: [{ type: "tool_use", id: toolId, name, input }] } });
export const back = (id: number, toolId: string, content: unknown, extra: Record<string, unknown> = {}, isError = false) =>
  ev(id, { type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: toolId, content, is_error: isError }] }, ...extra });
