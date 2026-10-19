import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ClaudeInventory, MachineDetail, MachineProjectView } from "../api/types";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const project = { key: "git:host/shop", name: "shop", source: "listed", lastSeenAt: new Date(2026, 9, 5, 9, 30).toISOString(), branch: null, headSha: null, tag: null, recentErrors: 0, lastRuns: [] } as MachineProjectView;
const inventory = (over: Partial<ClaudeInventory> = {}): ClaudeInventory => ({
  sessions: [{ name: "fix the login", projectKey: "git:host/shop", kind: "interactive", status: "idle", startedAt: new Date(2026, 9, 5, 9, 0).toISOString() }],
  plugins: [{ id: "adyusuf@adyusuf-marketplace", version: "3a60fbd", enabled: true, scope: "user" }, { id: "off@m", version: null, enabled: false, scope: null }],
  skills: ["mine", "p:review"],
  settings: [{ projectKey: "git:host/shop", mode: "B", modeModel: "sonnet", gates: { "push.prod": false, "merge.dev": true } }],
  at: new Date(2026, 9, 5, 10, 0).toISOString(), ...over,
});
const detail = (inv: ClaudeInventory | null | undefined, shared = inv != null): MachineDetail => ({ machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [project], inventory: inv, inventoryShared: shared });

function api(inv: ClaudeInventory | null | undefined, over: Parameters<typeof mockApi>[0] = {}, shared?: boolean, owner = "u1") {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: owner })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/detail": { body: detail(inv, shared) },
    "GET /agents/a1/asks": { body: [] },
    "GET /agents/a1/actions": { body: [] },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);

describe("the Claude inventory card on the machine page", () => {
  it("shows sessions with their project name, plugins with their state, skills and the project's mode and gates", async () => {
    api(inventory());
    open();
    expect(await screen.findByText("fix the login")).toBeInTheDocument();
    expect(screen.getAllByText("shop").length).toBeGreaterThan(0);
    expect(screen.getByText("adyusuf@adyusuf-marketplace")).toBeInTheDocument();
    expect(screen.getByText(en.machines.inventory.enabled)).toBeInTheDocument();
    expect(screen.getByText(en.machines.inventory.disabled)).toBeInTheDocument();
    expect(screen.getByText(en.machines.inventory.skillCount.replace("{n}", "2"))).toBeInTheDocument();
    expect(screen.getByText(en.machines.inventory.mode.replace("{mode}", "B"))).toBeInTheDocument();
    expect(screen.getByText("sonnet")).toBeInTheDocument();
    expect(screen.getByText(`push.prod: ${en.machines.inventory.no}`)).toBeInTheDocument();
    expect(screen.getByText(`merge.dev: ${en.machines.inventory.yes}`)).toBeInTheDocument();
  });

  it("has no card for a member who is not the owner when the API sends none", async () => {
    api(null, {}, false, "someone-else");
    open();
    await screen.findByText("shop");
    expect(screen.queryByText(en.machines.inventory.title)).toBeNull();
  });

  it("tells the owner it is off and turns it on with the switch, asking for a code when the API wants one", async () => {
    let n = 0;
    const calls = api(null, { "PUT /agents/a1/inventory": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: {} }) });
    open();
    expect(await screen.findByText(en.machines.inventory.off)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: en.machines.inventory.turnOn }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await userEvent.click(screen.getByRole("button", { name: en.machines.inventory.turnOn }));
    await screen.findByText(en.machines.inventory.saved);
    expect(calls.filter((c) => c.method === "PUT" && c.path === "/agents/a1/inventory").map((c) => c.body)).toEqual([{ share: true }, { share: true, code: "123456" }]);
  });

  it("says it is waiting for the agent when the switch is on and nothing has been reported, and offers turning it off", async () => {
    const calls = api(null, { "PUT /agents/a1/inventory": { body: {} } }, true);
    open();
    expect(await screen.findByText(en.machines.inventory.waiting)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: en.machines.inventory.turnOff }));
    await screen.findByText(en.machines.inventory.saved);
    expect(calls.filter((c) => c.method === "PUT" && c.path === "/agents/a1/inventory").map((c) => c.body)).toEqual([{ share: false }]);
  });

  it("shows an admin who is not the owner the inventory while it is on but no switch", async () => {
    api(inventory(), {}, true, "someone-else");
    open();
    expect(await screen.findByText("fix the login")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.machines.inventory.turnOff })).toBeNull();
  });

  it("says unknown for a list the agent could not read and none for an empty one", async () => {
    api(inventory({ sessions: null, plugins: [], skills: [], settings: [] }));
    open();
    const card = (await screen.findByText(en.machines.inventory.title)).closest(".card") as HTMLElement;
    expect(within(card).getByText(en.machines.inventory.unknown)).toBeInTheDocument();
    expect(within(card).getByText(en.machines.inventory.noPlugins)).toBeInTheDocument();
    expect(within(card).getByText(en.machines.inventory.noSkills)).toBeInTheDocument();
    expect(within(card).getByText(en.machines.inventory.noSettings)).toBeInTheDocument();
  });

  it("shows a session name that looks like markup as plain text", async () => {
    api(inventory({ sessions: [{ name: "<img src=x onerror=alert(1)>", projectKey: null, kind: null, status: null, startedAt: null }] }));
    const { container } = open();
    expect(await screen.findByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
    expect(container.querySelector("img[src='x']")).toBeNull();
  });
});
