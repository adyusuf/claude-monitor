import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineDetail, MachineProjectView } from "../api/types";
import { de } from "../i18n/de";
import { en } from "../i18n/en";
import { tr } from "../i18n/tr";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const project = (over: Partial<MachineProjectView> = {}): MachineProjectView => ({
  key: "git:host/shop", name: "shop", source: "listed", lastSeenAt: new Date(2026, 9, 5, 9, 30).toISOString(), branch: null, headSha: null, tag: null, recentErrors: 0, lastRuns: [],
  hasTest: true, testArgv: ["/usr/bin/dotnet", "test", "--no-build"], testCwd: "/work/shop", ...over,
}) as MachineProjectView;
const detail = (projects: MachineProjectView[]): MachineDetail => ({ machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects });

function api(projects: MachineProjectView[], owner = "u1", over: Parameters<typeof mockApi>[0] = {}) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: owner })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/detail": { body: detail(projects) },
    "GET /agents/a1/asks": { body: [] },
    "GET /agents/a1/actions": { body: [] },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);

describe("start the test of a project on the machine page", () => {
  it("shows the owner the command the machine reported and starts it by naming only the project", async () => {
    const calls = api([project()], "u1", { "POST /agents/a1/test-runs": { body: { id: "r1", status: "pending_approval" } } });
    open();
    expect(await screen.findByText("/usr/bin/dotnet test --no-build")).toBeInTheDocument();
    expect(screen.getByText(en.machines.projects.testIn.replace("{dir}", "/work/shop"))).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: en.machines.projects.startTest }));

    expect(await screen.findByText(en.machines.projects.testAsked)).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "POST" && c.path === "/agents/a1/test-runs").map((c) => c.body)).toEqual([{ projectKey: "git:host/shop" }]);
  });

  it("offers nothing to a member who is not the machine's owner, even when there is a command", async () => {
    api([project({ testArgv: null, testCwd: null })], "someone-else");
    open();
    await screen.findByText("shop");
    expect(screen.queryByRole("button", { name: en.machines.projects.startTest })).toBeNull();
  });

  it("offers nothing for a project with no test command", async () => {
    api([project({ hasTest: false, testArgv: null, testCwd: null })]);
    open();
    await screen.findByText("shop");
    expect(screen.queryByRole("button", { name: en.machines.projects.startTest })).toBeNull();
  });

  it("says why when the API refuses", async () => {
    api([project()], "u1", { "POST /agents/a1/test-runs": { status: 409, body: { title: "no_test_command" } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: en.machines.projects.startTest }));
    expect(await screen.findByText(en.errors.no_test_command)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.projects.testAsked)).toBeNull();
  });

  it("has its texts and error messages in all three languages with the same placeholders", () => {
    for (const lang of [en, tr, de]) {
      expect(lang.machines.projects.startTest.length).toBeGreaterThan(0);
      expect(lang.machines.projects.testIn).toContain("{dir}");
      expect(lang.errors.no_test_command.length).toBeGreaterThan(0);
    }
  });
});
