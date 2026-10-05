import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CommandRow, EventRow, SessionDetail, SessionRow } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { tr } from "../i18n/tr";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { SessionPage } from "./SessionPage";

const T0 = Date.UTC(2026, 9, 5, 12, 0, 0);
const at = (seconds: number) => new Date(T0 + seconds * 1000).toISOString();

const session = (): SessionRow => ({
  id: "s1", harnessKind: "claude_code", title: "Fix the login", model: "m", gitBranch: "main", status: "active",
  startedAt: at(0), lastEventAt: new Date().toISOString(), endedAt: null, projectId: "p", projectName: "widgets",
  agentId: "a", hostname: "laptop-1", ownerId: "u1", ownerName: "Örnek Kişi", costUsd: 1.5, openPermissions: 0,
});
const detail: SessionDetail = { session: session(), canCommand: true, tasks: [], subagents: [], usage: [] };

let nextId = 100;
const ev = (kind: string, payload: Record<string, unknown>, seconds: number, id = nextId--): EventRow =>
  ({ id, kind, occurredAt: at(seconds), truncated: false, payload });
const command = (over: Partial<CommandRow> = {}): CommandRow => ({
  id: "c1", kind: "prompt", body: "run the tests", status: "applied", createdBy: "u1", createdAt: at(80), expiresAt: at(900),
  deliveredAt: at(85), appliedAt: at(90), result: null, ...over,
});

interface Setup { events?: EventRow[]; commands?: CommandRow[] | (() => CommandRow[]); next?: string | null; older?: EventRow[] }

function open({ events = [], commands = [], next = null, older = [] }: Setup = {}) {
  const calls = mockApi({
    "GET /me": { body: ME },
    "GET /sessions/s1": { body: detail },
    "GET /sessions/s1/permission-requests": { body: [] },
    "GET /sessions/s1/commands": () => ({ body: typeof commands === "function" ? commands() : commands }),
    "GET /sessions/s1/events": (c) => ({ body: c.path.includes("before=") ? { items: older, next: null } : { items: events, next } }),
  });
  const view = renderAt("/w/w1/sessions/s1", [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }]);
  return { calls, view };
}

const activityCard = async () => (await screen.findByRole("heading", { name: "Activity" })).closest("section")!;

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const mixed = [
  ev("hook:UserPromptSubmit", { prompt: "please fix the login" }, 100),
  ev("transcript", { type: "assistant", message: { content: [{ type: "text", text: "On it, looking at the form." }] } }, 99),
  ev("hook:PreToolUse", { tool_name: "Bash", tool_input: { command: "npm test" } }, 98),
  ev("hook:PostToolUse", { tool_name: "Bash", tool_input: { command: "npm test" } }, 97),
  ev("usage", { model: "m" }, 96),
];

describe("the activity list tells inputs from everything else", () => {
  it("shows a web command and a typed prompt apart, each with a label, an icon and a status, in text and not by colour alone", async () => {
    open({ events: mixed, commands: [command()] });
    const card = await activityCard();
    const monitor = (await within(card).findByText("run the tests")).closest("li")!;
    const human = within(card).getByText("please fix the login").closest("li")!;

    expect(monitor).toHaveClass("act-monitor_input");
    expect(within(monitor).getByText("Claude Monitor (web)")).toBeInTheDocument();
    expect(within(monitor).getByText("sent by Örnek Kişi")).toBeInTheDocument();
    expect(within(monitor).getByText("Applied")).toBeInTheDocument();
    expect(within(monitor).getByText("\u2601\uFE0E")).toBeInTheDocument();

    expect(human).toHaveClass("act-human_input");
    expect(within(human).getByText("Human (Claude Code)")).toBeInTheDocument();
    expect(within(human).getByText("typed by Örnek Kişi")).toBeInTheDocument();
    expect(within(human).getByText("\u2328\uFE0E")).toBeInTheDocument();

    expect(within(card).getByText("On it, looking at the form.").closest("li")).toHaveClass("act-assistant");
    expect(card.querySelectorAll(".act-input")).toHaveLength(2);
  });

  it("calls a command from someone other than the owner by a neutral name", async () => {
    open({ commands: [command({ createdBy: "someone-else" })] });
    expect(await screen.findByText("sent by a member")).toBeInTheDocument();
  });

  it("shows a stop request with its own icon and no text of its own", async () => {
    open({ commands: [command({ kind: "stop", body: null })] });
    const item = (await screen.findByText("Stop request")).closest("li")!;
    expect(item).toHaveClass("act-monitor_input");
    expect(within(item).getByText("■")).toBeInTheDocument();
  });

  it("folds tool calls and background events into one closed group that opens on press", async () => {
    open({ events: mixed });
    const card = await activityCard();
    const toggle = await within(card).findByRole("button", { name: /Tool calls: 2 · Other events: 1/ });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(within(card).queryByText("Bash · npm test")).not.toBeInTheDocument();
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(within(card).getAllByText("Bash · npm test")).toHaveLength(2);
  });

  it("does not crash on an event kind it has never seen, and shows it as a quiet line", async () => {
    open({ events: [ev("hook:SomethingNew", { x: 1 }, 50)] });
    const line = (await screen.findByText("SomethingNew")).closest("li")!;
    expect(line).toHaveClass("act-quiet");
  });

  it("opens an event's payload under its line", async () => {
    open({ events: mixed });
    await userEvent.click(await screen.findByText("please fix the login"));
    expect(screen.getByText(/"prompt":"please fix the login"/)).toBeInTheDocument();
  });
});

describe("what is not a human input", () => {
  it("never shows a tool result as a typed message, and a prompt only once", async () => {
    open({
      events: [
        ev("hook:UserPromptSubmit", { prompt: "fix it" }, 100),
        ev("transcript", { type: "user", message: { role: "user", content: "fix it" } }, 101),
        ev("transcript", { type: "user", message: { role: "user", content: [{ type: "tool_result", content: "42 passed" }] } }, 102),
      ],
    });
    const card = await activityCard();
    await within(card).findByText("fix it");
    expect(within(card).getAllByText("fix it")).toHaveLength(1);
    expect(card.querySelectorAll(".act-human_input")).toHaveLength(1);
  });

  it("shows a web command once: the transcript echo of its text is dropped, and it sits next to the prompt it rode along with", async () => {
    open({
      events: [
        ev("hook:UserPromptSubmit", { prompt: "please fix the login" }, 90, 20),
        ev("transcript", { type: "attachment", attachment: { content: ["anything wrapped around: run the tests"] } }, 91, 21),
        ev("hook:PreToolUse", { tool_name: "Bash" }, 95, 22),
      ],
      commands: [command({ appliedAt: at(91) })],
    });
    const card = await activityCard();
    await within(card).findByText("run the tests");
    expect(card.textContent).not.toContain("anything wrapped around");
    const inputs = [...card.querySelectorAll(".act-input")].map((li) => li.className);
    expect(inputs[0]).toContain("act-monitor_input");
    expect(inputs[1]).toContain("act-human_input");
    expect(within(card).getByText("delivered together with the message typed in the session")).toBeInTheDocument();
  });
});

describe("a command that has not entered the session yet", () => {
  it("stays on top as waiting until it is applied, then takes its place in the timeline", async () => {
    let state: CommandRow[] = [command({ id: "w", body: "pending one", status: "queued", appliedAt: null, deliveredAt: null, createdAt: at(200) })];
    open({ events: mixed, commands: () => state });
    const card = await activityCard();
    const waiting = (await within(card).findByText("pending one")).closest("li")!;
    expect(within(card).getByText("Waiting to be applied")).toBeInTheDocument();
    expect(within(waiting).getByText("Waiting")).toBeInTheDocument();
    expect(waiting).toHaveClass("act-pending");
    expect(card.querySelector(".act-input")).toBe(waiting); // the first input line of the whole card

    state = [command({ id: "w", body: "pending one", status: "applied", appliedAt: at(60), deliveredAt: at(59) })];
    act(() => FakeEventSource.last!.emit("session", { sessionId: "s1" }));
    await waitFor(() => expect(within(card).queryByText("Waiting to be applied")).not.toBeInTheDocument());
    const applied = within(card).getByText("pending one").closest("li")!;
    expect(applied).not.toHaveClass("act-pending");
    expect(within(applied).getByText("Applied")).toBeInTheDocument();
    const order = [...card.querySelectorAll(".act-input, .act-assistant")].map((li) => li.textContent);
    expect(order[0]).toContain("please fix the login");
    expect(order[1]).toContain("On it, looking at the form.");
    expect(order[2]).toContain("pending one"); // applied at 60: older than both, so it sits below them
  });

  it("keeps a waiting command visible under the Inputs filter and away from the Assistant one", async () => {
    open({ events: mixed, commands: [command({ body: "waiting body", status: "delivered", appliedAt: null })] });
    const card = await activityCard();
    await within(card).findByText("waiting body");
    await userEvent.click(within(card).getByRole("button", { name: "Assistant" }));
    expect(within(card).queryByText("waiting body")).not.toBeInTheDocument();
    await userEvent.click(within(card).getByRole("button", { name: "Inputs" }));
    expect(within(card).getByText("waiting body")).toBeInTheDocument();
  });
});

describe("the filter chips", () => {
  const show = () => open({ events: mixed, commands: [command()] });

  it("show the right subset, and Inputs shows only the two input kinds", async () => {
    show();
    const card = await activityCard();
    await within(card).findByText("run the tests");
    const chip = (name: string) => within(card).getByRole("button", { name });

    await userEvent.click(chip("Inputs"));
    expect(chip("Inputs")).toHaveAttribute("aria-pressed", "true");
    expect(card.querySelectorAll(".act-input")).toHaveLength(2);
    expect(card.querySelector(".act-assistant, .act-quiet, .act-group")).toBeNull();

    await userEvent.click(chip("Assistant"));
    expect(card.querySelectorAll(".act-assistant")).toHaveLength(1);
    expect(card.querySelector(".act-input, .act-quiet, .act-group")).toBeNull();

    await userEvent.click(chip("Tools"));
    expect(within(card).getByRole("button", { name: /Tool calls: 2 · Other events: 1/ })).toBeInTheDocument();
    expect(card.querySelector(".act-input, .act-assistant")).toBeNull();

    await userEvent.click(chip("All"));
    expect(card.querySelectorAll(".act-input")).toHaveLength(2);
    expect(card.querySelectorAll(".act-assistant")).toHaveLength(1);
  });

  it("says so when the filter leaves nothing", async () => {
    open({ events: [ev("usage", {}, 1)] });
    const card = await activityCard();
    await userEvent.click(await within(card).findByRole("button", { name: "Inputs" }));
    expect(within(card).getByText("Nothing to show with this filter.")).toBeInTheDocument();
  });

  it("remember the choice across a reload", async () => {
    const first = show();
    await userEvent.click(await screen.findByRole("button", { name: "Inputs" }));
    expect(localStorage.getItem(config.activityFilterKey)).toBe("inputs");
    first.view.unmount();

    show();
    await waitFor(() => expect(screen.getByRole("button", { name: "Inputs" })).toHaveAttribute("aria-pressed", "true"));
  });

  it("work without storage: the choice lasts for the page only", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    show();
    expect(await screen.findByRole("button", { name: "All" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(screen.getByRole("button", { name: "Assistant" }));
    expect(screen.getByRole("button", { name: "Assistant" })).toHaveAttribute("aria-pressed", "true");
  });

  it("ignore a stored value that is not a filter", async () => {
    localStorage.setItem(config.activityFilterKey, "nonsense");
    show();
    expect(await screen.findByRole("button", { name: "All" })).toHaveAttribute("aria-pressed", "true");
  });
});

describe("pages of older activity", () => {
  it("holds a command that belongs to an unloaded page back, and shows it once that page is loaded", async () => {
    const events = [ev("hook:Notification", { message: "recent" }, 1000, 50)];
    const older = [ev("hook:UserPromptSubmit", { prompt: "an old prompt" }, 5, 10)];
    open({ events, next: "50", older, commands: [command({ body: "old command", appliedAt: at(6), createdAt: at(4) })] });
    const card = await activityCard();
    await within(card).findByText("recent");
    expect(within(card).queryByText("old command")).not.toBeInTheDocument();

    await userEvent.click(within(card).getByRole("button", { name: "Older activity" }));
    await within(card).findByText("an old prompt");
    const order = [...card.querySelectorAll(".act-input")].map((li) => li.textContent);
    expect(order[0]).toContain("old command");
    expect(order[1]).toContain("an old prompt");
  });
});

describe("the dictionaries", () => {
  const keys = (o: unknown, prefix = ""): string[] =>
    Object.entries(o as Record<string, unknown>).flatMap(([k, v]) => typeof v === "object" && v !== null ? keys(v, `${prefix}${k}.`) : [`${prefix}${k}`]);

  it("have the same keys in English and Turkish, and the activity strings are filled in", () => {
    expect(keys(tr).sort()).toEqual(keys(en).sort());
    for (const dict of [en, tr]) {
      for (const key of keys(dict.activity)) expect(key.length).toBeGreaterThan(0);
      for (const value of Object.values(dict.activity.status)) expect(value.trim()).not.toBe("");
    }
    expect(tr.activity.monitorInput).not.toBe("");
    expect(tr.activity.humanInput).not.toBe(en.activity.humanInput);
  });
});
