import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AskView, MachineDetail } from "../api/types";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const detail = (): MachineDetail => ({ machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [] });
const ask = (over: Partial<AskView> = {}): AskView => ({
  id: "k1", targetAgentId: "a1", targetHostname: "laptop-1", status: "answered", question: "Which branch?", answer: "main", answerTruncated: false, error: null,
  createdAt: new Date(2026, 9, 5, 9, 30).toISOString(), expiresAt: new Date(2026, 9, 5, 9, 32).toISOString(), deliveredAt: null, answeredAt: null, ...over,
});

function api(over: Parameters<typeof mockApi>[0] = {}, view: Partial<ReturnType<typeof machine>> = {}, userId = "u1") {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine(view)] },
    "GET /workspaces/w1/agents": { body: [agent({ userId })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/detail": { body: detail() },
    "GET /agents/a1/asks": { body: [] },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);
const puts = (calls: ReturnType<typeof mockApi>) => calls.filter((c) => c.method === "PUT" && c.path === "/agents/a1/asks");

describe("the asks card on the machine page", () => {
  it("tells the owner the machine takes no asks and turns it on with no code first", async () => {
    const calls = api({ "PUT /agents/a1/asks": { body: {} } });
    open();
    expect(await screen.findByText(en.machines.asks.off)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: en.machines.asks.turnOn }));
    expect(await screen.findByText(en.machines.asks.saved)).toBeInTheDocument();
    expect(puts(calls)[0]!.body).toEqual({ accept: true });
  });

  it("asks for a code when the API wants a recent sign-in and sends it on the second try", async () => {
    let n = 0;
    const calls = api({ "PUT /agents/a1/asks": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: {} }) });
    open();
    await userEvent.click(await screen.findByRole("button", { name: en.machines.asks.turnOn }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await userEvent.click(screen.getByRole("button", { name: en.machines.asks.turnOn }));
    await screen.findByText(en.machines.asks.saved);
    expect(puts(calls)[1]!.body).toEqual({ accept: true, code: "123456" });
  });

  it("shows the state and offers to stop when the machine takes asks", async () => {
    const calls = api({ "PUT /agents/a1/asks": { body: {} } }, { acceptsAsks: true });
    open();
    expect(await screen.findByText(en.machines.asks.on)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: en.machines.asks.turnOff }));
    await screen.findByText(en.machines.asks.saved);
    expect(puts(calls)[0]!.body).toEqual({ accept: false });
  });

  it("lists the asks sent here with their status and shows the question and the answer as plain text", async () => {
    api({ "GET /agents/a1/asks": { body: [ask({ question: "<img src=x onerror=alert(1)> **bold**", answer: "main", answerTruncated: true }), ask({ id: "k2", status: "no_idle_session", question: "Second?", answer: null })] } }, { acceptsAsks: true });
    const { container } = open();
    expect(await screen.findByText(/onerror/)).toBeInTheDocument();
    expect(container.querySelector("img")).toBeNull();
    expect(screen.getByText(en.machines.asks.status.answered)).toBeInTheDocument();
    expect(screen.getByText(en.machines.asks.status.no_idle_session)).toBeInTheDocument();
    expect(screen.getByText(`(${en.machines.asks.answerCut})`)).toBeInTheDocument();
  });

  it("says there is no ask yet", async () => {
    api();
    open();
    expect(await screen.findByText(en.machines.asks.none)).toBeInTheDocument();
  });

  it("shows someone who does not own the machine the state and no switch and no list", async () => {
    const calls = api({}, { acceptsAsks: true }, "u2");
    open();
    expect(await screen.findByText(en.machines.asks.ownerOnly)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.machines.asks.turnOff })).toBeNull();
    expect(calls.filter((c) => c.path === "/agents/a1/asks")).toHaveLength(0);
  });
});
