import { fireEvent, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentActionView, MachineDetail, MachineProjectView } from "../api/types";
import { de } from "../i18n/de";
import { en } from "../i18n/en";
import { tr } from "../i18n/tr";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { AUTO_STARTS_PER_HOUR, FIRST_PROMPT_MAX, runningSessions } from "./MachineClaude";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const project = (over: Partial<MachineProjectView> = {}): MachineProjectView => ({
  key: "git:host/shop", name: "shop", source: "session", lastSeenAt: new Date(2026, 9, 5, 9, 30).toISOString(), branch: null, headSha: null, tag: null, recentErrors: 0, lastRuns: [], ...over,
}) as MachineProjectView;
const detail = (projects: MachineProjectView[] = [project()]): MachineDetail => ({ machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects });
const action = (over: Partial<AgentActionView> = {}): AgentActionView => ({
  id: "x1", agentId: "a1", kind: "claude_start", status: "succeeded", result: "started session 34a6ea9d in the background (manual mode); it appears in the session list",
  createdAt: new Date(2026, 9, 5, 9, 30).toISOString(), expiresAt: new Date(2026, 9, 5, 9, 40).toISOString(), finishedAt: new Date(2026, 9, 5, 9, 31).toISOString(),
  projectKey: "git:host/shop", permissionMode: "manual", sessionRef: "34a6ea9d", ...over,
});

const dev = { roleEnvironment: "dev", effectiveEnvironment: "dev", acceptsClaudeControl: true } as const;

function api(over: Parameters<typeof mockApi>[0] = {}, view: Partial<ReturnType<typeof machine>> = {}, actions: AgentActionView[] = [], projects?: MachineProjectView[], version = "0.6.0") {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine(view)] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1", version })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/detail": { body: detail(projects) },
    "GET /agents/a1/asks": { body: [] },
    "GET /agents/a1/actions": { body: actions },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);
const posts = (calls: ReturnType<typeof mockApi>) => calls.filter((c) => c.method === "POST" && c.path === "/agents/a1/actions");

describe("the Claude card on the machine page", () => {
  it("is off until the owner turns it on, sends no code first and shows no start form", async () => {
    const calls = api({ "PUT /agents/a1/claude": { body: {} } }, { roleEnvironment: "dev", effectiveEnvironment: "dev" });
    open();
    expect(await screen.findByText(en.machines.claudeControl.off)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.claudeControl.newTitle)).toBeNull();

    await userEvent.click(await screen.findByRole("button", { name: en.machines.claudeControl.turnOn }));

    expect(await screen.findByText(en.machines.claudeControl.done.saved)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT" && c.path === "/agents/a1/claude")!.body).toEqual({ accept: true });
  });

  it("asks for a code when the API wants a recent sign-in and sends it on the second try", async () => {
    let n = 0;
    const calls = api({ "PUT /agents/a1/claude": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: {} }) }, { roleEnvironment: "test", effectiveEnvironment: "test" });
    open();
    await userEvent.click(await screen.findByRole("button", { name: en.machines.claudeControl.turnOn }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await userEvent.click(screen.getByRole("button", { name: en.machines.claudeControl.turnOn }));
    await screen.findByText(en.machines.claudeControl.done.saved);
    expect(calls.filter((c) => c.method === "PUT" && c.path === "/agents/a1/claude")[1]!.body).toEqual({ accept: true, code: "123456" });
  });

  it("starts a session in the chosen project with the chosen mode and says it was asked", async () => {
    const calls = api({ "POST /agents/a1/actions": { body: action({ status: "queued", sessionRef: null, result: null }) } }, dev, [], [project(), project({ key: "git:host/blog", name: "blog" })]);
    open();
    await userEvent.selectOptions(await screen.findByLabelText(en.machines.claudeControl.project), "git:host/blog");
    await userEvent.selectOptions(screen.getByLabelText(en.machines.claudeControl.mode), "plan");

    await userEvent.click(screen.getByRole("button", { name: en.machines.claudeControl.start }));

    expect(await screen.findByText(en.machines.claudeControl.done.started)).toBeInTheDocument();
    expect(posts(calls)[0]!.body).toEqual({ kind: "claude_start", projectKey: "git:host/blog", permissionMode: "plan" });
  });

  it("sends a first prompt with the start, trimmed, and clears the box; an empty box sends none", async () => {
    const calls = api({ "POST /agents/a1/actions": { body: action({ status: "queued", sessionRef: null, result: null }) } }, dev);
    open();
    const box = await screen.findByLabelText(new RegExp(en.machines.claudeControl.firstPrompt.replace(/[\\()]/g, "\\$&")));
    await userEvent.click(await screen.findByRole("button", { name: en.machines.claudeControl.start }));
    expect(await screen.findByText(en.machines.claudeControl.done.started)).toBeInTheDocument();
    expect(posts(calls)[0]!.body).toEqual({ kind: "claude_start", projectKey: "git:host/shop", permissionMode: "manual" }); // no firstPrompt key at all

    fireEvent.change(box, { target: { value: "  fix the failing test\n" } });
    await userEvent.click(screen.getByRole("button", { name: en.machines.claudeControl.start }));

    await vi.waitFor(() => expect(posts(calls)).toHaveLength(2));
    expect(posts(calls)[1]!.body).toEqual({ kind: "claude_start", projectKey: "git:host/shop", permissionMode: "manual", firstPrompt: "fix the failing test" });
    await vi.waitFor(() => expect((box as HTMLTextAreaElement).value).toBe(""));
  });

  it("counts the characters of the first prompt, says how it is sent, and keeps Start off over the limit", async () => {
    api({}, dev);
    open();
    const box = await screen.findByLabelText(new RegExp(en.machines.claudeControl.firstPrompt.replace(/[\\()]/g, "\\$&")));
    expect(screen.getByText(en.machines.claudeControl.firstPromptHint)).toBeInTheDocument();
    expect(screen.getByText(`0 / ${FIRST_PROMPT_MAX}`)).toBeInTheDocument();

    fireEvent.change(box, { target: { value: "x".repeat(FIRST_PROMPT_MAX) } });
    expect(screen.getByText(`${FIRST_PROMPT_MAX} / ${FIRST_PROMPT_MAX}`)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.claudeControl.start })).toBeEnabled();

    fireEvent.change(box, { target: { value: "x".repeat(FIRST_PROMPT_MAX + 1) } });
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByRole("button", { name: en.machines.claudeControl.start })).toBeDisabled();
  });

  it("does not let a project Claude Code does not trust be chosen, starts with the first one that is not known untrusted, and says which are trusted", async () => {
    const calls = api({ "POST /agents/a1/actions": { body: action({ status: "queued", sessionRef: null, result: null }) } }, dev, [], [
      project({ key: "git:host/old", name: "old", trusted: false }), project({ key: "git:host/blog", name: "blog", trusted: true }), project({ key: "git:host/new", name: "new", trusted: null }),
    ]);
    open();
    const select = await screen.findByLabelText(en.machines.claudeControl.project);
    const options = [...select.querySelectorAll("option")];
    expect(options.map((o) => [o.getAttribute("value"), (o as HTMLOptionElement).disabled])).toEqual([["git:host/old", true], ["git:host/blog", false], ["git:host/new", false]]);
    expect(options[0]!.textContent).toBe(`old (${en.machines.claudeControl.untrustedOption})`);
    expect((select as HTMLSelectElement).value).toBe("git:host/blog");
    expect(screen.getByText(en.machines.claudeControl.trusted)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: en.machines.claudeControl.start }));

    await vi.waitFor(() => expect(posts(calls)).toHaveLength(1));
    expect(posts(calls)[0]!.body).toMatchObject({ projectKey: "git:host/blog" });
  });

  it("says so and keeps Start off when Claude Code trusts none of the projects", async () => {
    api({}, dev, [], [project({ trusted: false })]);
    open();
    expect(await screen.findByText(en.machines.claudeControl.allUntrusted)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.claudeControl.start })).toBeDisabled();
  });

  it("shows the automatic start off by default, says what it costs and turns it on with the mode in the request", async () => {
    const calls = api({ "PUT /agents/a1/claude": { body: {} } }, dev);
    open();
    const select = await screen.findByLabelText(en.machines.claudeControl.auto);
    expect(select).toHaveValue("off");
    expect(screen.getByText(en.machines.claudeControl.autoHint.replace("{n}", String(AUTO_STARTS_PER_HOUR)))).toBeInTheDocument();

    await userEvent.selectOptions(select, "on_message");

    expect(await screen.findByText(en.machines.claudeControl.done.auto)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT" && c.path === "/agents/a1/claude")!.body).toEqual({ accept: true, autoMode: "on_message" });
  });

  it("shows the mode the machine says it has and turns it off with off", async () => {
    const calls = api({ "PUT /agents/a1/claude": { body: {} } }, { ...dev, claudeAutoMode: "on_message" });
    open();
    const select = await screen.findByLabelText(en.machines.claudeControl.auto);
    expect(select).toHaveValue("on_message");

    await userEvent.selectOptions(select, "off");

    await vi.waitFor(() => expect(calls.find((c) => c.method === "PUT" && c.path === "/agents/a1/claude")).toBeTruthy());
    expect(calls.find((c) => c.method === "PUT" && c.path === "/agents/a1/claude")!.body).toEqual({ accept: true, autoMode: "off" });
  });

  it("keeps a session running in a project: choosing always sends the first project that is not known untrusted, and the project can be changed", async () => {
    const calls = api({ "PUT /agents/a1/claude": { body: {} } }, dev, [], [
      project({ key: "git:host/old", name: "old", trusted: false }), project({ key: "git:host/blog", name: "blog", trusted: true }), project({ key: "git:host/new", name: "new" }),
    ]);
    open();

    await userEvent.selectOptions(await screen.findByLabelText(en.machines.claudeControl.auto), "always");

    await vi.waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/agents/a1/claude")).toBe(true));
    expect(calls.find((c) => c.method === "PUT" && c.path === "/agents/a1/claude")!.body).toEqual({ accept: true, autoMode: "always", autoProject: "git:host/blog" });
  });

  it("shows the project of the always mode, lists only projects Claude Code may start in, and changing it sends the new one", async () => {
    const calls = api({ "PUT /agents/a1/claude": { body: {} } }, { ...dev, claudeAutoMode: "always", claudeAutoProject: "git:host/blog" }, [], [
      project({ key: "git:host/old", name: "old", trusted: false }), project({ key: "git:host/blog", name: "blog", trusted: true }), project({ key: "git:host/new", name: "new" }),
    ]);
    open();
    const projectSelect = await screen.findByLabelText(en.machines.claudeControl.autoProject);
    expect(projectSelect).toHaveValue("git:host/blog");
    expect([...projectSelect.querySelectorAll("option")].map((o) => o.getAttribute("value"))).toEqual(["git:host/blog", "git:host/new"]);

    await userEvent.selectOptions(projectSelect, "git:host/new");

    await vi.waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/agents/a1/claude")).toBe(true));
    expect(calls.find((c) => c.method === "PUT" && c.path === "/agents/a1/claude")!.body).toEqual({ accept: true, autoMode: "always", autoProject: "git:host/new" });
  });

  it("does not offer always when Claude Code trusts no project, and offers no project choice for the other modes", async () => {
    api({}, { ...dev, claudeAutoMode: "on_message" }, [], [project({ trusted: false })]);
    open();
    const select = await screen.findByLabelText(en.machines.claudeControl.auto);
    expect([...select.querySelectorAll("option")].find((o) => o.getAttribute("value") === "always")).toBeDisabled();
    expect(screen.queryByLabelText(en.machines.claudeControl.autoProject)).toBeNull();
  });

  it("shows no automatic start on a machine where starting Claude is off or not allowed", async () => {
    api({}, { roleEnvironment: "dev", effectiveEnvironment: "dev", acceptsClaudeControl: false });
    open();
    await screen.findByText(en.machines.claudeControl.off);
    expect(screen.queryByLabelText(en.machines.claudeControl.auto)).toBeNull();
  });

  it("offers only the three careful modes and never bypass, auto or dontAsk", async () => {
    api({}, dev);
    open();
    const select = await screen.findByLabelText(en.machines.claudeControl.mode);
    expect([...select.querySelectorAll("option")].map((o) => o.getAttribute("value"))).toEqual(["manual", "plan", "acceptEdits"]);
  });

  it("lists the sessions started here that nobody stopped and stops one by its id", async () => {
    const calls = api({ "POST /agents/a1/actions": { body: action({ kind: "claude_stop", status: "queued", result: null }) } }, dev, [
      action({ id: "s1", sessionRef: "34a6ea9d" }), action({ id: "s2", sessionRef: "0123abcd", projectKey: "git:host/blog" }),
      action({ id: "p1", kind: "claude_stop", sessionRef: "0123abcd", result: "stopped session 0123abcd; its conversation is kept" }),
    ]);
    open();
    expect(await screen.findByText("34a6ea9d")).toBeInTheDocument();
    expect(screen.queryByText("0123abcd", { selector: "code" })).toBeNull(); // stopped

    await userEvent.click(screen.getByRole("button", { name: en.machines.claudeControl.stop }));

    expect(await screen.findByText(en.machines.claudeControl.done.stopped)).toBeInTheDocument();
    expect(posts(calls)[0]!.body).toEqual({ kind: "claude_stop", sessionRef: "34a6ea9d" });
  });

  it("shows no start on a production or unset machine and says why", async () => {
    api({}, { roleEnvironment: "production", effectiveEnvironment: "production", acceptsClaudeControl: true });
    open();
    expect(await screen.findByText(en.machines.claudeControl.notAllowed)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.claudeControl.newTitle)).toBeNull();
  });

  it("shows no start on a machine a claim made stricter than what the admin set", async () => {
    api({}, { roleEnvironment: "dev", effectiveEnvironment: "production", acceptsClaudeControl: true });
    open();
    expect(await screen.findByText(en.machines.claudeControl.notAllowed)).toBeInTheDocument();
  });

  it("says an older agent ignores it and keeps the start button off", async () => {
    api({}, dev, [], undefined, "0.5.0");
    open();
    expect(await screen.findByText(en.machines.claudeControl.oldAgent.replace("{min}", "0.6.0"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.claudeControl.start })).toBeDisabled();
  });

  it("says a machine with no project has nothing to start and hides the form", async () => {
    api({}, dev, [], []);
    open();
    expect(await screen.findByText(en.machines.claudeControl.noProjects)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.claudeControl.start })).toBeDisabled();
  });

  it("shows another member only the owner-only line", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1/machines": { body: [machine(dev)] }, "GET /workspaces/w1/agents": { body: [agent({ userId: "u2", version: "0.6.0" })] },
      "GET /workspaces/w1/alerts": { body: [] }, "GET /workspaces/w1/runs": { body: [] }, "GET /agents/a1/grants": { body: [] }, "GET /agents/a1/jobs": { body: [] },
      "GET /agents/a1/metrics": { body: [] }, "GET /agents/a1/detail": { body: detail() }, "GET /agents/a1/asks": { body: [] },
    });
    open();
    expect(await screen.findByText(en.machines.claudeControl.ownerOnly)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.machines.claudeControl.start })).toBeNull();
  });

  it("lists the Claude actions with their results as text", async () => {
    api({}, dev, [action({ status: "failed", sessionRef: null, result: "Claude Code does not trust that folder yet: <b>open Claude there</b>" })]);
    const { container } = open();
    expect(await screen.findByText(/does not trust that folder yet/)).toBeInTheDocument();
    expect(container.querySelector("b")).toBeNull();
  });
});

describe("running sessions", () => {
  it("are the successful starts that name a session and have no successful stop", () => {
    const list = [
      action({ id: "a", sessionRef: "11111111" }), action({ id: "b", sessionRef: "22222222" }), action({ id: "c", kind: "claude_stop", sessionRef: "22222222" }),
      action({ id: "d", status: "failed", sessionRef: null }), action({ id: "e", kind: "claude_stop", status: "failed", sessionRef: "11111111" }), action({ id: "f", sessionRef: null }),
    ];
    expect(runningSessions(list).map((a) => a.id)).toEqual(["a"]);
  });
});

describe("the texts", () => {
  it("exist in all three languages with the same placeholders, and the machine card is not left to show a key", () => {
    for (const dict of [en, tr, de]) {
      expect(dict.machines.claudeControl.oldAgent).toContain("{min}");
      expect(Object.keys(dict.machines.claudeControl.modes)).toEqual(["manual", "plan", "acceptEdits"]);
      expect(dict.machines.actions.kind.claude_start).not.toBe("");
      expect(dict.errors.claude_control_environment).not.toBe("");
      for (const key of ["invalid_first_prompt", "claude_folder_not_trusted", "claude_start_pending"] as const) expect(dict.errors[key]).not.toBe("");
      expect(dict.machines.claudeControl.autoHint).toContain("{n}");
      for (const key of ["auto", "autoHint", "autoProject"] as const) expect(dict.machines.claudeControl[key]).not.toBe("");
      expect(Object.keys(dict.machines.claudeControl.autoModes)).toEqual(["off", "on_message", "always"]);
      expect(dict.errors.invalid_auto_project).not.toBe("");
      expect(dict.machines.claudeControl.done.auto).not.toBe("");
      expect(dict.errors.invalid_auto_mode).not.toBe("");
      expect(dict.machines.claudeControl.firstPromptCount).toContain("{n}");
      expect(dict.machines.claudeControl.firstPromptCount).toContain("{max}");
      for (const key of ["firstPrompt", "firstPromptHint", "untrustedOption", "trusted", "allUntrusted"] as const) expect(dict.machines.claudeControl[key]).not.toBe("");
    }
  });
});
