import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, openTab, renderAt } from "../test/helpers";
import { grant, job, machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const at = (i: number) => new Date(2026, 9, 5, 9, 59 - i).toISOString();
const fullGrants = Array.from({ length: config.remotePageSize }, (_, i) => grant({ id: `g${i}`, createdAt: at(i) }));
const fullJobs = Array.from({ length: config.remotePageSize }, (_, i) => job({ id: `j${i}`, name: `job-${i}`, createdAt: at(i) }));

function api(over: Parameters<typeof mockApi>[0] = {}) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);

describe("older grants and jobs of a machine", () => {
  it("reads grants a page at a time and offers More for the older ones, with before and beforeId = the oldest shown (F-341)", async () => {
    const calls = api({
      "GET /agents/a1/grants": (c) => c.path.includes("before=")
        ? { body: [grant({ id: "ancient", granteeName: "Old Grantee", status: "active", createdAt: new Date(2026, 6, 1).toISOString() })] }
        : { body: fullGrants },
    });
    open();
    await openTab(en.views.machine.access);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));

    expect(await screen.findByText("Old Grantee")).toBeInTheDocument();
    expect(calls.find((c) => c.path.startsWith("/agents/a1/grants"))!.path).toBe(`/agents/a1/grants?limit=${config.remotePageSize}`);
    expect(calls.find((c) => c.path.includes("before="))!.path)
      .toBe(`/agents/a1/grants?before=${encodeURIComponent(fullGrants[fullGrants.length - 1]!.createdAt)}&beforeId=${fullGrants[fullGrants.length - 1]!.id}&limit=${config.remotePageSize}`);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull(); // a short page: nothing older
  });

  it("reads jobs a page at a time and offers More for the older ones", async () => {
    const calls = api({
      "GET /agents/a1/jobs": (c) => c.path.includes("before=") ? { body: [job({ id: "ancient", name: "ancient-job", createdAt: new Date(2026, 6, 1).toISOString() })] } : { body: fullJobs },
    });
    open();
    await openTab(en.views.machine.work);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));

    expect(await screen.findByText("ancient-job")).toBeInTheDocument();
    expect(calls.find((c) => c.path.includes("before="))!.path)
      .toBe(`/agents/a1/jobs?before=${encodeURIComponent(fullJobs[fullJobs.length - 1]!.createdAt)}&beforeId=${fullJobs[fullJobs.length - 1]!.id}&limit=${config.remotePageSize}`);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });

  it("offers no More when fewer than a page came back", async () => {
    api({ "GET /agents/a1/grants": { body: [grant()] }, "GET /agents/a1/jobs": { body: [job()] } });
    open();
    await openTab(en.views.machine.access);
    await screen.findByText(en.remote.grants);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
    await openTab(en.views.machine.work);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });
});
