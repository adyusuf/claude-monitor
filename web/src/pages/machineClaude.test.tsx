import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentActionView, MachineDetail, MachineProjectView } from "../api/types";
import { de } from "../i18n/de";
import { en } from "../i18n/en";
import { tr } from "../i18n/tr";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { runningSessions } from "./MachineClaude";
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
    }
  });
});
