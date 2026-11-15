import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { done } from "../test/runs-fixtures";
import { agent } from "./machinesFixture";
import { RunsPage } from "./RunsPage";

const route = [{ path: "/w/:ws/runs", element: <RunsPage /> }];
const rowOf = (id: string) => screen.getByText(`/bin/echo ${id}`).closest("tr") as HTMLElement;
const cells = (row: HTMLElement) => within(row).getAllByRole("cell");

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

describe("the runs page list", () => {
  it("shows each run with its status, command, machine, requester and when, newest first as the API sent them", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [] },
      "GET /workspaces/w1/runs": { body: [done("r2", 1, { status: "failed" }), done("r1", 10)] },
    });
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    const rows = screen.getAllByRole("row").slice(1);
    expect(rows.map((r) => within(r).getByText(/^\/bin\/echo/).textContent)).toEqual(["/bin/echo r2", "/bin/echo r1"]);
    const first = cells(rows[0]!);
    expect(first[0]).toHaveTextContent(en.remote.status.failed);
    expect(first[2]).toHaveTextContent("build-box svc");
    expect(first[3]).toHaveTextContent("Diğer Kişi laptop-9");
    expect(first[4]).toHaveTextContent(/^05\/10\/2026 11:59$/);
  });

  it("says nothing has run yet when the workspace has no run", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [] }, "GET /workspaces/w1/runs": { body: [] } });
    renderAt("/w/w1/runs", route);
    expect(await screen.findByText(en.runsPage.empty)).toBeInTheDocument();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("shows the error text when the first page cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [] }, "GET /workspaces/w1/runs": { status: 500, body: {} } });
    renderAt("/w/w1/runs", route);
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("still lists the runs when the machine filter's list cannot be read, with the filter holding only 'All machines'", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { status: 500, body: {} },
      "GET /workspaces/w1/runs": { body: [done("r1", 1)] },
    });
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    expect(within(screen.getByRole("combobox", { name: en.runsPage.machine })).getAllByRole("option").map((o) => o.textContent)).toEqual([en.runsPage.allMachines]);
  });
});

describe("filtering the runs page by machine", () => {
  const serve = () => mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/agents": { body: [agent({ id: "a1", hostname: "laptop-1", userName: "Diğer Kişi" })] },
    "GET /workspaces/w1/runs": (c) => ({ body: c.path.includes("agent=a1") ? [done("mine", 1)] : [done("any", 1), done("mine", 2)] }),
  });

  it("offers each machine as 'host · owner' after 'All machines'", async () => {
    serve();
    renderAt("/w/w1/runs", route);
    const select = await screen.findByRole("combobox", { name: en.runsPage.machine });
    await waitFor(() => expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual([en.runsPage.allMachines, "laptop-1 · Diğer Kişi"]));
  });

  it("asks the API for that machine's runs only and replaces the list with them", async () => {
    const calls = serve();
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo any");
    await waitFor(() => expect(screen.getByRole("option", { name: "laptop-1 · Diğer Kişi" })).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByRole("combobox", { name: en.runsPage.machine }), "a1");
    await waitFor(() => expect(screen.queryByText("/bin/echo any")).toBeNull());
    expect(screen.getByText("/bin/echo mine")).toBeInTheDocument();
    expect(calls.filter((c) => c.path.startsWith("/workspaces/w1/runs")).at(-1)!.path).toBe(`/workspaces/w1/runs?agent=a1&limit=${config.runPageSize}`);
  });

  it("goes back to every machine's runs when 'All machines' is chosen again", async () => {
    serve();
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo any");
    await waitFor(() => expect(screen.getByRole("option", { name: "laptop-1 · Diğer Kişi" })).toBeInTheDocument());
    await userEvent.selectOptions(screen.getByRole("combobox", { name: en.runsPage.machine }), "a1");
    await waitFor(() => expect(screen.queryByText("/bin/echo any")).toBeNull());
    await userEvent.selectOptions(screen.getByRole("combobox", { name: en.runsPage.machine }), "");
    expect(await screen.findByText("/bin/echo any")).toBeInTheDocument();
  });
});

describe("who decided a run, in the list", () => {
  const decided = async (over: Partial<WebRunView>) => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [] }, "GET /workspaces/w1/runs": { body: [done("r1", 1, over)] } });
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    return cells(rowOf("r1"))[5]!;
  };

  it("names a grant when a grant allowed it, even though the API also records the owner as the decider", async () => {
    expect(await decided({ grantId: "g1", decidedByName: "Örnek Kişi" })).toHaveTextContent(en.runsPage.byGrant);
  });

  it("names a job when a job allowed it", async () => {
    expect(await decided({ jobId: "j1", decidedByName: "Örnek Kişi" })).toHaveTextContent(en.runsPage.byJob);
  });

  it("names the person and when they decided, for a run a person decided", async () => {
    const decidedAt = new Date(2026, 9, 5, 9, 30).toISOString();
    expect(await decided({ decidedByName: "Örnek Kişi", decidedAt })).toHaveTextContent("Örnek Kişi · 05/10/2026 09:30");
  });

  it("names the person alone when the time of the decision is not known", async () => {
    expect(await decided({ decidedByName: "Örnek Kişi", decidedAt: null })).toHaveTextContent(/^Örnek Kişi$/);
  });

  it("says 'not yet' for a run that still waits for a decision", async () => {
    expect(await decided({ status: "pending_approval", canDecide: false })).toHaveTextContent(en.runsPage.undecided);
  });

  it("shows a dash for a run that expired without a decision", async () => {
    expect(await decided({ status: "expired" })).toHaveTextContent(/^—$/);
  });
});

describe("loading older runs", () => {
  const page = (from: number) => Array.from({ length: config.runPageSize }, (_, i) => done(`p${from + i}`, from + i));

  it("offers 'Load more' only for a full first page, and asks for the runs older than the oldest one shown", async () => {
    const first = page(0);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [] },
      "GET /workspaces/w1/runs": (c) => ({ body: c.path.includes("before=") ? [done("older", 200)] : first }),
    });
    renderAt("/w/w1/runs", route);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));
    expect(await screen.findByText("/bin/echo older")).toBeInTheDocument();
    const oldest = first[first.length - 1]!;
    // The cursor is the oldest run's time and id (F-167): runs created at the same moment are not skipped across the page boundary.
    expect(calls.find((c) => c.path.includes("before="))!.path).toBe(`/workspaces/w1/runs?before=${encodeURIComponent(oldest.createdAt)}&beforeId=${encodeURIComponent(oldest.id)}&limit=${config.runPageSize}`);
    expect(screen.getAllByRole("row")).toHaveLength(config.runPageSize + 2);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });

  it("does not offer 'Load more' for a first page shorter than a page", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { body: [] }, "GET /workspaces/w1/runs": { body: [done("r1", 1)] } });
    renderAt("/w/w1/runs", route);
    await screen.findByText("/bin/echo r1");
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });

  it("does not list a run twice when the older page repeats one already shown", async () => {
    const first = page(0);
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [] },
      "GET /workspaces/w1/runs": (c) => ({ body: c.path.includes("before=") ? [first[first.length - 1]!, done("older", 200)] : first }),
    });
    renderAt("/w/w1/runs", route);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));
    await screen.findByText("/bin/echo older");
    expect(screen.getAllByRole("row")).toHaveLength(config.runPageSize + 2);
  });

  it("shows the error and keeps the runs shown when the older page cannot be read", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [] },
      "GET /workspaces/w1/runs": (c) => (c.path.includes("before=") ? { status: 500, body: {} } : { body: page(0) }),
    });
    renderAt("/w/w1/runs", route);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.getAllByRole("row")).toHaveLength(config.runPageSize + 1);
  });
});
