import { describe, expect, it } from "vitest";
import type { CommandRow, EventRow } from "../api/types";
import { config } from "../config";
import { ActivityClass, classifyEvent, summary, transcriptUserText } from "./activity";
import { ActivityFilter, buildTimeline, filterItems, groupRows, isFilter } from "./timeline";

const T0 = Date.UTC(2026, 9, 5, 12, 0, 0);
const at = (seconds: number) => new Date(T0 + seconds * 1000).toISOString();

let nextId = 1;
const ev = (kind: string, payload: Record<string, unknown>, seconds = 0, id = nextId++): EventRow =>
  ({ id, kind, occurredAt: at(seconds), truncated: false, payload });
const transcript = (payload: Record<string, unknown>, seconds = 0, id?: number) => ev("transcript", payload, seconds, id);
const userLine = (content: unknown, seconds = 0, extra: Record<string, unknown> = {}) =>
  transcript({ type: "user", message: { role: "user", content }, ...extra }, seconds);
const cmd = (over: Partial<CommandRow> = {}): CommandRow => ({
  id: "c1", kind: "prompt", body: "run the tests", status: "applied", createdBy: "u1", createdAt: at(-30), expiresAt: at(600),
  deliveredAt: at(-5), appliedAt: at(0), result: null, ...over,
});

const classes = (events: EventRow[], commands: CommandRow[] = [], hasOlder = false) =>
  buildTimeline(events, commands, hasOlder).items.map((i) => i.cls);

describe("classifying events", () => {
  it("puts every kind in its class", () => {
    const table: [EventRow, string][] = [
      [ev("hook:UserPromptSubmit", { prompt: "hello" }), ActivityClass.HumanInput],
      [ev("hook:PreToolUse", { tool_name: "Bash" }), ActivityClass.Tool],
      [ev("hook:PostToolUse", {}), ActivityClass.Tool],
      [ev("hook:PostToolUseFailure", {}), ActivityClass.Tool],
      [ev("hook:PermissionRequest", {}), ActivityClass.Tool],
      [ev("hook:Notification", { message: "x" }), ActivityClass.Meta],
      [ev("hook:SubagentStart", {}), ActivityClass.Meta],
      [ev("hook:SubagentStop", {}), ActivityClass.Meta],
      [ev("usage", {}), ActivityClass.Meta],
      [ev("note", { text: "n" }), ActivityClass.Meta],
      [transcript({ type: "assistant", message: { content: [{ type: "text", text: "Done." }] } }), ActivityClass.Assistant],
      [transcript({ type: "assistant", message: { content: [{ type: "tool_use", name: "Bash", input: { command: "ls" } }] } }), ActivityClass.Tool],
      [transcript({ type: "assistant", message: { content: [{ type: "thinking", thinking: "hm" }] } }), ActivityClass.Meta],
      [userLine([{ type: "tool_result", content: "ok" }]), ActivityClass.Tool],
      [userLine("typed text"), ActivityClass.HumanInput],
      [userLine([{ type: "text", text: "typed in blocks" }]), ActivityClass.HumanInput],
      [userLine("injected", 0, { isMeta: true }), ActivityClass.Meta],
      [userLine("<task-notification>\n<task-id>b1</task-id> done</task-notification>"), ActivityClass.Meta],
      [userLine("<command-name>/clear</command-name>"), ActivityClass.Meta],
      [userLine([{ type: "text", text: "<system-reminder>be brief</system-reminder>" }]), ActivityClass.Meta],
      [userLine("<pasted_content id=\"1\">log text</pasted_content> what is wrong?"), ActivityClass.HumanInput],
      [userLine("a < b and <b>bold</b> are fine to type"), ActivityClass.HumanInput],
      [transcript({ type: "attachment", attachment: {} }), ActivityClass.Meta],
      [transcript({ type: "summary" }), ActivityClass.Meta],
    ];
    for (const [event, expected] of table) expect([event.kind, event.payload, classifyEvent(event)]).toEqual([event.kind, event.payload, expected]);
  });

  it("puts a kind it has never seen in meta and does not throw", () => {
    expect(classifyEvent(ev("hook:SomethingNew", { x: 1 }))).toBe(ActivityClass.Meta);
    expect(classifyEvent(ev("brand_new_kind", {}))).toBe(ActivityClass.Meta);
    expect(classifyEvent(ev("transcript", {}))).toBe(ActivityClass.Meta);
    expect(classifyEvent(ev("transcript", { type: "user", message: { content: 42 } }))).toBe(ActivityClass.Meta);
    expect(summary(ev("brand_new_kind", {}))).toBe("");
  });

  it("reads a typed text only from a user line that is not a tool result", () => {
    expect(transcriptUserText(userLine("  hi  "))).toBe("hi");
    expect(transcriptUserText(userLine([{ type: "tool_result", content: "x" }, { type: "text", text: "side" }]))).toBe("");
    expect(transcriptUserText(userLine("x", 0, { isMeta: true }))).toBe("");
    expect(transcriptUserText(ev("hook:UserPromptSubmit", { prompt: "p" }))).toBe("");
  });

  it("summarises a tool call that carries no text", () => {
    expect(summary(transcript({ type: "assistant", message: { content: [{ type: "tool_use", name: "Read", input: { file_path: "/a.ts" } }] } }))).toBe("Read · /a.ts");
    expect(summary(userLine("typed"))).toBe("typed");
  });
});

describe("one timeline for events and web commands", () => {
  it("shows a web command as a monitor input and a typed prompt as a human input", () => {
    const { items } = buildTimeline([ev("hook:UserPromptSubmit", { prompt: "typed" }, 100)], [cmd({ appliedAt: at(10) })], false);
    expect(items.map((i) => [i.cls, i.text])).toEqual([[ActivityClass.HumanInput, "typed"], [ActivityClass.MonitorInput, "run the tests"]]);
  });

  it("shows a stop request as a monitor input without text", () => {
    const { items } = buildTimeline([], [cmd({ kind: "stop", body: null })], false);
    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({ cls: ActivityClass.MonitorInput, text: "" });
    expect(items[0]!.command?.kind).toBe("stop");
  });

  it("does not show a prompt twice: the hook wins over the transcript line with the same text", () => {
    const list = classes([ev("hook:UserPromptSubmit", { prompt: "fix it" }, 1), userLine("fix it", 2)]);
    expect(list).toEqual([ActivityClass.HumanInput]);
  });

  it("matches one transcript line to one hook, so a repeated prompt shows as many times as it was typed", () => {
    const events = [
      ev("hook:UserPromptSubmit", { prompt: "yes" }, 1), userLine("yes", 2), ev("hook:UserPromptSubmit", { prompt: "yes" }, 9), userLine("yes", 10),
    ];
    expect(classes(events)).toEqual([ActivityClass.HumanInput, ActivityClass.HumanInput]);
  });

  it("keeps a typed transcript line that has no prompt hook (a harness without the hook)", () => {
    expect(classes([userLine("typed without a hook")])).toEqual([ActivityClass.HumanInput]);
  });

  it("never makes a line the harness wrote (a task notice, a command echo) a human input", () => {
    const notice = userLine("<task-notification>done</task-notification>", 5);
    expect(classes([notice])).toEqual([ActivityClass.Meta]);
    expect(transcriptUserText(notice)).toBe("");
  });

  it("drops the transcript twin of a prompt hook even when the prompt begins with a tag", () => {
    const text = "<div>why does this break?</div>";
    expect(classes([ev("hook:UserPromptSubmit", { prompt: text }, 1), userLine(text, 2)])).toEqual([ActivityClass.HumanInput]);
  });

  it("never makes a tool result a human input", () => {
    expect(classes([userLine([{ type: "tool_result", content: "42 passed" }])])).toEqual([ActivityClass.Tool]);
  });

  it("drops the transcript echo of an applied web command, whatever its wrapper says", () => {
    const body = "run the tests";
    const echoes = [
      transcript({ type: "attachment", attachment: { type: "anything", content: [`something new: - ${body}`] } }, 1),
      userLine(`Stop hook feedback: ${body}`, 2),
      userLine([{ type: "text", text: body }], 3),
    ];
    const kept = ev("hook:Notification", { message: "other" }, 4);
    const { items } = buildTimeline([...echoes, kept], [cmd({ body })], false);
    expect(items.map((i) => i.key).sort()).toEqual([`e${kept.id}`, "cc1"].sort());
  });

  it("drops the attachments Claude Code really writes for a hook's context (shapes seen in real transcripts)", () => {
    const body = "run the tests";
    const context = `Messages sent to this session from Claude Monitor (the web):\n- ${body}`;
    const hookOutput = JSON.stringify({ hookSpecificOutput: { hookEventName: "UserPromptSubmit", additionalContext: context } });
    const added = transcript({ type: "attachment", attachment: { type: "hook_additional_context", content: [context], hookName: "UserPromptSubmit", hookEvent: "UserPromptSubmit", toolUseID: "t" } }, 1);
    const success = transcript({ type: "attachment", attachment: { type: "hook_success", content: "", stdout: hookOutput, stderr: "", exitCode: 0, command: "cm-agent hook", hookEvent: "UserPromptSubmit", hookName: "UserPromptSubmit", toolUseID: "t", durationMs: 12 } }, 2);
    const { items } = buildTimeline([added, success], [cmd({ body })], false);
    expect(items.map((i) => i.key)).toEqual(["cc1"]);
  });

  it("keeps an attachment that does not repeat the command, or that is far from its moment", () => {
    const body = "run the tests";
    const other = transcript({ type: "attachment", attachment: { content: ["something else"] } }, 1);
    const far = transcript({ type: "attachment", attachment: { content: [body] } }, config.commandEchoSeconds + 60);
    const { items } = buildTimeline([other, far], [cmd({ body })], false);
    expect(items.map((i) => i.key)).toContain(`e${other.id}`);
    expect(items.map((i) => i.key)).toContain(`e${far.id}`);
  });

  it("does not hide an assistant line because it mentions the command's text", () => {
    const body = "run the tests";
    const reply = transcript({ type: "assistant", message: { content: [{ type: "text", text: `I will ${body} now` }] } }, 1);
    expect(buildTimeline([reply], [cmd({ body })], false).items.map((i) => i.cls)).toContain(ActivityClass.Assistant);
  });

  it("pins commands that wait on top, and puts them in the timeline when they are applied", () => {
    const events = [ev("hook:Notification", { message: "n" }, 50)];
    const waiting = cmd({ id: "w", status: "queued", appliedAt: null, createdAt: at(60) });
    const before = buildTimeline(events, [waiting], false);
    expect(before.pinned.map((i) => i.key)).toEqual(["cw"]);
    expect(before.pinned[0]!.pending).toBe(true);
    expect(before.items.map((i) => i.key)).toEqual([`e${events[0]!.id}`]);
    const delivered = buildTimeline(events, [{ ...waiting, status: "delivered" }], false);
    expect(delivered.pinned).toHaveLength(1);

    const after = buildTimeline(events, [{ ...waiting, status: "applied", appliedAt: at(70) }], false);
    expect(after.pinned).toHaveLength(0);
    expect(after.items.map((i) => i.key)).toEqual(["cw", `e${events[0]!.id}`]);
  });

  it("leaves an expired or cancelled command in the timeline, at the moment it was created", () => {
    const { pinned, items } = buildTimeline([ev("usage", {}, 5)], [cmd({ id: "x", status: "expired", appliedAt: null, createdAt: at(30) })], false);
    expect(pinned).toHaveLength(0);
    expect(items[0]).toMatchObject({ key: "cx", at: T0 + 30_000, pending: false });
  });

  it("puts a web command applied with a typed prompt directly above that prompt", () => {
    const prompt = ev("hook:UserPromptSubmit", { prompt: "typed" }, 100, 10);
    const after = ev("hook:PreToolUse", { tool_name: "Bash" }, 101, 11);
    const before = ev("hook:Notification", { message: "n" }, 99, 9);
    const { items } = buildTimeline([before, prompt, after], [cmd({ appliedAt: at(103) })], false);
    expect(items.map((i) => i.key)).toEqual(["e11", "cc1", "e10", "e9"]);
    expect(items[1]).toMatchObject({ withHuman: true, at: items[2]!.at });
  });

  it("does not pair a command with a prompt typed long before or after it", () => {
    const prompt = ev("hook:UserPromptSubmit", { prompt: "typed" }, 100);
    const { items } = buildTimeline([prompt], [cmd({ appliedAt: at(100 + config.commandPairSeconds + 5) })], false);
    expect(items.every((i) => !i.withHuman)).toBe(true);
    expect(items[0]!.key).toBe("cc1");
  });

  it("pairs a command with the closest typed prompt", () => {
    const far = ev("hook:UserPromptSubmit", { prompt: "a" }, 95, 1);
    const close = ev("hook:UserPromptSubmit", { prompt: "b" }, 99, 2);
    const { items } = buildTimeline([far, close], [cmd({ appliedAt: at(100) })], false);
    expect(items.map((i) => i.key)).toEqual(["cc1", "e2", "e1"]);
  });
});

describe("pages of older activity", () => {
  const recent = ev("hook:Notification", { message: "recent" }, 1000, 50);
  const old = cmd({ id: "old", appliedAt: at(10), createdAt: at(5) });
  const inside = cmd({ id: "inside", appliedAt: at(1010), createdAt: at(1005) });

  it("holds back a command older than the loaded page while older pages remain", () => {
    const { items } = buildTimeline([recent], [old, inside], true);
    expect(items.map((i) => i.key)).toEqual(["cinside", "e50"]);
  });

  it("shows it once nothing older remains, in its place", () => {
    const { items } = buildTimeline([recent], [old, inside], false);
    expect(items.map((i) => i.key)).toEqual(["cinside", "e50", "cold"]);
  });

  it("shows it as soon as the older page that reaches its moment is loaded", () => {
    const olderPage = ev("hook:Notification", { message: "older" }, 5, 40);
    const { items } = buildTimeline([recent, olderPage], [old], true);
    expect(items.map((i) => i.key)).toEqual(["e50", "cold", "e40"]);
  });

  it("still pins a waiting command whichever page is loaded", () => {
    const waiting = cmd({ id: "w", status: "queued", appliedAt: null, createdAt: at(-9000) });
    expect(buildTimeline([recent], [waiting], true).pinned).toHaveLength(1);
  });
});

describe("filtering and grouping", () => {
  const events = [
    ev("hook:UserPromptSubmit", { prompt: "typed" }, 50),
    transcript({ type: "assistant", message: { content: [{ type: "text", text: "answer" }] } }, 40),
    ev("hook:PreToolUse", { tool_name: "Bash" }, 30),
    ev("hook:PostToolUse", { tool_name: "Bash" }, 29),
    ev("usage", {}, 28),
    ev("hook:Notification", { message: "n" }, 20),
  ];
  const { items } = buildTimeline(events, [cmd({ id: "m", appliedAt: at(60) })], false);
  const classesOf = (filter: Parameters<typeof filterItems>[1]) => [...new Set(filterItems(items, filter).map((i) => i.cls))].sort();

  it("shows the right subset for each filter", () => {
    expect(classesOf(ActivityFilter.All)).toEqual(Object.values(ActivityClass).sort());
    expect(classesOf(ActivityFilter.Inputs)).toEqual([ActivityClass.HumanInput, ActivityClass.MonitorInput].sort());
    expect(classesOf(ActivityFilter.Assistant)).toEqual([ActivityClass.Assistant]);
    expect(classesOf(ActivityFilter.Tools)).toEqual([ActivityClass.Meta, ActivityClass.Tool].sort());
  });

  it("recognises a stored filter and nothing else", () => {
    expect(isFilter("inputs")).toBe(true);
    expect(isFilter("nonsense")).toBe(false);
    expect(isFilter(null)).toBe(false);
  });

  it("folds runs of tool and meta lines into groups and leaves a lone one as a line", () => {
    const rows = groupRows(items);
    expect(rows.map((r) => r.kind)).toEqual(["item", "item", "item", "group"]);
    const group = rows[3]!;
    expect(group.kind === "group" && [group.items.length, group.tools, group.others]).toEqual([4, 2, 2]);
    expect(group.kind === "group" && group.key).toBe(`g${items[items.length - 1]!.key}`);
    expect(groupRows([])).toEqual([]);
    const lone = groupRows(items.filter((i) => i.cls === ActivityClass.Assistant || i.key === items.find((x) => x.cls === ActivityClass.Tool)!.key));
    expect(lone.map((r) => r.kind)).toEqual(["item", "item"]);
  });
});
