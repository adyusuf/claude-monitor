import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, openTab, renderAt } from "../test/helpers";
import { job, machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const at = (i: number) => new Date(2026, 9, 5, 9, 59 - i).toISOString();
const statusOf = (path: string) => new URL(path, "http://test.local").searchParams.get("status") ?? "";
/** A full page of finished jobs: the newest ones, so an older active job is not on the first page of the unfiltered list. */
const finished = Array.from({ length: config.remotePageSize }, (_, i) => job({ id: `f${i}`, name: `finished-${i}`, status: "retired", createdAt: at(i) }));
const oldActive = job({ id: "old", name: "old-active-job", status: "active", argv: ["/usr/bin/backup"], createdAt: new Date(2026, 6, 1).toISOString() });
const waiting = job({ id: "w", name: "waiting-job", status: "proposed", createdAt: at(100) });

function api() {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/schedules": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/jobs": (c) => {
      switch (statusOf(c.path)) {
        case "active":
          return { body: [oldActive] };
        case "open":
          return { body: [waiting, oldActive] };
        case "finished":
          return { body: finished };
        default:
          return { body: finished };
      }
    },
  });
}

describe("the job status filter (F-138)", () => {
  it("reads the list again with the chosen status and shows only what came back", async () => {
    const calls = api();
    renderAt("/w/w1/machines/a1", route);
    await openTab(en.views.machine.work);
    expect(await screen.findByText("finished-0")).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText(en.remote.jobFilter.label), "open");

    expect(await screen.findByText("waiting-job")).toBeInTheDocument();
    expect(screen.queryByText("finished-0")).toBeNull();
    expect(calls.some((c) => c.path.startsWith("/agents/a1/jobs") && statusOf(c.path) === "open")).toBe(true);
    await userEvent.selectOptions(screen.getByLabelText(en.remote.jobFilter.label), "");
    expect(await screen.findByText("finished-0")).toBeInTheDocument();
    expect(screen.queryByText("waiting-job")).toBeNull();
  });

  it("offers the schedule picker an active job older than the first page, whatever the list is filtered to", async () => {
    api();
    renderAt("/w/w1/machines/a1", route);
    await openTab(en.views.machine.work);
    await screen.findByText("finished-0");

    await userEvent.selectOptions(screen.getByLabelText(en.remote.jobFilter.label), "finished");

    await waitFor(() => expect(screen.getByRole("option", { name: "old-active-job" })).toBeInTheDocument());
    expect(screen.queryByText(en.schedules.noJobs)).toBeNull();
  });
});
