import { screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineDetail } from "../api/types";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const detail: MachineDetail = { machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [] };

/** Every path the machine page reads when it opens, with the number of times (F-097: all the tabs stay mounted, so a second read would repeat each of them). */
describe("the machine page's first load", () => {
  it("reads each of its sources once: the reload waits for the signed-in user instead of firing everything before and after it", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/machines": { body: [machine()] },
      "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
      "GET /workspaces/w1/alerts": { body: [] },
      "GET /workspaces/w1/runs": { body: [] },
      "GET /agents/a1/grants": { body: [] },
      "GET /agents/a1/jobs": { body: [] },
      "GET /agents/a1/detail": { body: detail },
      "GET /agents/a1/schedules": { body: [] },
      "GET /agents/a1/metrics": { body: [] },
      "GET /agents/a1/asks": { body: [] },
      "GET /agents/a1/actions": { body: [] },
      "GET /agents/a1/exec-level": { body: { agentId: "a1", execLevel: "off", sequence: 0, report: null, reportAt: null, pending: null } },
    });

    renderAt("/w/w1/machines/a1", [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }]);
    await screen.findByRole("heading", { level: 1 });
    await waitFor(() => expect(calls.some((c) => c.path.startsWith("/agents/a1/actions"))).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 100)); // a late second read would show up here

    const counts = new Map<string, number>();
    for (const c of calls) counts.set(c.path.split("?")[0]!, (counts.get(c.path.split("?")[0]!) ?? 0) + 1);
    const repeated = [...counts].filter(([, n]) => n > 1).map(([path, n]) => `${path} x${n}`);
    // Two sources are read twice on purpose: the jobs (the list and the schedule picker use different status filters) and the agent's actions (the actions card
    // and the Claude card each list their own kinds). Everything else is read once; before the reload waited for the user, 24 requests were made, now 15.
    expect(repeated).toEqual(["/agents/a1/jobs x2", "/agents/a1/actions x2"]);
    expect(calls.filter((c) => c.path.startsWith("/workspaces/w1/runs")).length).toBe(1);
    expect(calls.length).toBe(15);
  });
});
