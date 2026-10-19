import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentActionKind, AgentActionStatus, AgentActionView, MachineDetail } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent, count, settle } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const detail = (): MachineDetail => ({ machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [] });
const row = (over: Partial<AgentActionView> = {}): AgentActionView => ({
  id: "x1", agentId: "a1", kind: "status", status: "succeeded", result: null,
  createdAt: new Date(2026, 9, 5, 9, 30).toISOString(), expiresAt: new Date(2026, 9, 5, 9, 40).toISOString(), finishedAt: null, ...over,
});

interface Setup { view?: Partial<ReturnType<typeof machine>>; userId?: string; version?: string; list?: AgentActionView[] }

function api(over: Parameters<typeof mockApi>[0] = {}, { view = {}, userId = "u1", version = "0.5.0", list = [] }: Setup = {}) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine(view)] },
    "GET /workspaces/w1/agents": { body: [agent({ userId, version })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/detail": { body: detail() },
    "GET /agents/a1/asks": { body: [] },
    "GET /agents/a1/actions": { body: list },
    ...over,
  });
}
const open = () => renderAt("/w/w1/machines/a1", route);
const posts = (calls: ReturnType<typeof mockApi>) => calls.filter((c) => c.method === "POST" && c.path === "/agents/a1/actions");
const button = (kind: AgentActionKind) => screen.findByRole("button", { name: en.machines.actions.kind[kind] });
const sentText = (kind: AgentActionKind) => en.machines.actions.sent.replace("{action}", en.machines.actions.kind[kind]);
const rowOf = (inside: HTMLElement) => within(inside.closest("li") as HTMLElement);

describe("the agent actions card on the machine page", () => {
  it("shows the owner the four buttons and an empty list", async () => {
    api();
    open();
    expect(await screen.findByText(en.machines.actions.none)).toBeInTheDocument();
    for (const kind of ["status", "update_check", "update", "restart"] as const) {
      expect(await button(kind)).toBeEnabled();
    }
    expect(screen.getByText(en.machines.actions.hint)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.actions.ownerOnly)).toBeNull();
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull(); // no code field until the API asks for one
  });

  it("shows someone who does not own the machine only the owner-only sentence: no buttons, no list read", async () => {
    const calls = api({}, { userId: "u2" });
    open();
    expect(await screen.findByText(en.machines.actions.ownerOnly)).toBeInTheDocument();
    await settle(calls); // the signed-in user is known by now: the owner check has run against the real user
    expect(screen.getByText(en.machines.actions.ownerOnly)).toBeInTheDocument();
    for (const kind of ["status", "update_check", "update", "restart"] as const) {
      expect(screen.queryByRole("button", { name: en.machines.actions.kind[kind] })).toBeNull();
    }
    expect(screen.queryByText(en.machines.actions.none)).toBeNull();
    expect(count(calls, "/agents/a1/actions")).toBe(0);
  });

  it("sends the status request with no code and says it was asked", async () => {
    const calls = api({ "POST /agents/a1/actions": { body: row({ status: "queued" }) } });
    open();
    await userEvent.click(await button("status"));
    expect(await screen.findByText(sentText("status"))).toBeInTheDocument();
    expect(posts(calls)).toHaveLength(1);
    expect(posts(calls)[0]!.body).toEqual({ kind: "status" }); // toEqual would also pass on code: undefined, which JSON drops; a value fails it
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });

  it("reads the list again after asking, and the kind that is now waiting cannot be asked twice", async () => {
    let asked = false;
    const calls = api({
      "POST /agents/a1/actions": () => { asked = true; return { body: row({ id: "x2", kind: "update_check", status: "queued" }) }; },
      "GET /agents/a1/actions": () => ({ body: asked ? [row({ id: "x2", kind: "update_check", status: "queued" })] : [] }),
    });
    open();
    const check = await button("update_check");
    await userEvent.click(check);
    await waitFor(() => expect(screen.getByRole("button", { name: en.machines.actions.kind.update_check })).toBeDisabled());
    expect(screen.getByText(en.machines.actions.status.queued)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.actions.none)).toBeNull();
    expect(screen.getByRole("button", { name: en.machines.actions.kind.status })).toBeEnabled(); // only the kind that is waiting
    expect(posts(calls)).toHaveLength(1);
  });

  it("asks for a code when the API wants a recent sign-in, and sends it on the second try of restart", async () => {
    let n = 0;
    const calls = api({ "POST /agents/a1/actions": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: row({ kind: "restart", status: "queued" }) }) });
    open();
    await userEvent.click(await button("restart"));
    const field = await screen.findByLabelText(en.remote.codeLabel);
    expect(screen.getByText(en.machines.actions.enableHint)).toBeInTheDocument();
    expect(screen.queryByText(sentText("restart"))).toBeNull();
    expect(screen.getByRole("button", { name: en.machines.actions.kind.restart })).toBeDisabled(); // waits for the code
    await userEvent.type(field, "123456");
    await userEvent.click(screen.getByRole("button", { name: en.machines.actions.kind.restart }));
    expect(await screen.findByText(sentText("restart"))).toBeInTheDocument();
    expect(posts(calls)).toHaveLength(2);
    expect(posts(calls)[0]!.body).toEqual({ kind: "restart" });
    expect(posts(calls)[1]!.body).toEqual({ kind: "restart", code: "123456" });
  });

  it("says the code is not valid when the second try is refused for a code again", async () => {
    api({ "POST /agents/a1/actions": { status: 403, body: { title: "reauth_required" } } });
    open();
    await userEvent.click(await button("update"));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "000000");
    await userEvent.click(screen.getByRole("button", { name: en.machines.actions.kind.update }));
    expect(await screen.findByText(en.errors.invalid_code)).toBeInTheDocument();
    expect(screen.queryByText(sentText("update"))).toBeNull();
  });

  it("shows any other refusal as it is and asks for no code", async () => {
    api({ "POST /agents/a1/actions": { status: 409, body: { title: "conflict" } } });
    open();
    await userEvent.click(await button("status"));
    expect(await screen.findByText(en.errors.conflict)).toBeInTheDocument();
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
    expect(screen.queryByText(sentText("status"))).toBeNull();
  });

  it("sends the code only with restart and update, never with status or update check, even when one is typed", async () => {
    let n = 0;
    const calls = api({ "POST /agents/a1/actions": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: row({ status: "queued" }) }) });
    open();
    await userEvent.click(await button("restart")); // the 403 opens the code field
    const field = await screen.findByLabelText(en.remote.codeLabel);
    await userEvent.type(field, "123456");
    await userEvent.click(screen.getByRole("button", { name: en.machines.actions.kind.status }));
    await screen.findByText(sentText("status"));
    expect(posts(calls)).toHaveLength(2);
    expect(posts(calls)[1]!.body).toEqual({ kind: "status" });
    expect(Object.keys(posts(calls)[1]!.body as object)).toEqual(["kind"]); // the typed code is not on the wire
  });

  it("on a production machine shows the code field and its strict hint from the start, and sends the code only with the kinds that change the machine", async () => {
    const calls = api({ "POST /agents/a1/actions": { body: row({ status: "queued" }) } }, { view: { effectiveEnvironment: "production" } });
    open();
    const field = await screen.findByLabelText(en.remote.codeLabel);
    expect(screen.getByText(en.machines.actions.strictHint)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.actions.enableHint)).toBeNull();

    await userEvent.type(field, "111111");
    await userEvent.click(await button("status"));
    await screen.findByText(sentText("status"));
    await userEvent.type(screen.getByLabelText(en.remote.codeLabel), "222222");
    await userEvent.click(screen.getByRole("button", { name: en.machines.actions.kind.update_check }));
    await waitFor(() => expect(posts(calls)).toHaveLength(2));
    await userEvent.type(screen.getByLabelText(en.remote.codeLabel), "333333");
    await userEvent.click(screen.getByRole("button", { name: en.machines.actions.kind.update }));
    await waitFor(() => expect(posts(calls)).toHaveLength(3));
    await userEvent.type(screen.getByLabelText(en.remote.codeLabel), "444444");
    await userEvent.click(screen.getByRole("button", { name: en.machines.actions.kind.restart }));
    await waitFor(() => expect(posts(calls)).toHaveLength(4));

    expect(posts(calls).map((c) => c.body)).toEqual([
      { kind: "status" },
      { kind: "update_check" },
      { kind: "update", code: "333333" },
      { kind: "restart", code: "444444" },
    ]);
  });

  it("on a production machine keeps Status and Check for update usable without a code while Update and Restart wait for one", async () => {
    api({}, { view: { effectiveEnvironment: "production" } });
    open();
    await screen.findByLabelText(en.remote.codeLabel);
    expect(await button("status")).toBeEnabled();
    expect(await button("update_check")).toBeEnabled();
    expect(await button("update")).toBeDisabled();
    expect(await button("restart")).toBeDisabled();
  });

  it("links the code field to the authenticator guide", async () => {
    api({}, { view: { effectiveEnvironment: "production" } });
    open();
    await screen.findByLabelText(en.remote.codeLabel);
    expect(screen.getByRole("link", { name: en.twoStep.help })).toHaveAttribute("href", "/two-step");
  });

  it("treats a database machine as strict too", async () => {
    api({}, { view: { effectiveFunction: "database" } });
    open();
    expect(await screen.findByLabelText(en.remote.codeLabel)).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.strictHint)).toBeInTheDocument();
  });

  it("warns that an agent older than the first one with actions ignores them, and not for a current one", async () => {
    api({}, { version: "0.4.4" });
    const { unmount } = open();
    await button("status");
    expect(screen.getByText(en.machines.actions.oldAgent.replace("{min}", config.actionsMinAgentVersion))).toBeInTheDocument();
    unmount();
    vi.restoreAllMocks();

    api({}, { version: "0.5.0" });
    open();
    await button("status");
    expect(screen.queryByText(en.machines.actions.oldAgent.replace("{min}", config.actionsMinAgentVersion))).toBeNull();
  });

  it("warns about an agent whose version is not known", async () => {
    api({ "GET /workspaces/w1/agents": { body: [agent({ userId: "u1", version: "" })] } });
    open();
    await button("status");
    expect(screen.getByText(en.machines.actions.oldAgent.replace("{min}", config.actionsMinAgentVersion))).toBeInTheDocument();
  });

  it("disables only the kind that has an action waiting or delivered, not one that is finished", async () => {
    api({}, {
      list: [
        row({ id: "q", kind: "update", status: "queued" }),
        row({ id: "d", kind: "restart", status: "delivered" }),
        row({ id: "s", kind: "status", status: "succeeded" }),
        row({ id: "f", kind: "update_check", status: "failed" }),
        row({ id: "e", kind: "status", status: "expired" }),
      ],
    });
    open();
    await waitFor(() => expect(screen.getByRole("button", { name: en.machines.actions.kind.update })).toBeDisabled());
    expect(screen.getByRole("button", { name: en.machines.actions.kind.restart })).toBeDisabled();
    expect(screen.getByRole("button", { name: en.machines.actions.kind.status })).toBeEnabled();
    expect(screen.getByRole("button", { name: en.machines.actions.kind.update_check })).toBeEnabled();
  });

  it("lists each action with its status label, and shows the result the agent wrote as plain text", async () => {
    const finished: { status: AgentActionStatus; kind: AgentActionKind }[] = [
      { status: "succeeded", kind: "status" }, { status: "failed", kind: "update" }, { status: "expired", kind: "restart" },
    ];
    api({}, { list: finished.map((f) => row({ id: `r-${f.status}`, kind: f.kind, status: f.status, result: `result-${f.status}` })) });
    open();
    for (const f of finished) {
      const line = rowOf(await screen.findByText(`result-${f.status}`));
      expect(line.getByText(en.machines.actions.status[f.status])).toBeInTheDocument();
      expect(line.getByText(en.machines.actions.kind[f.kind])).toBeInTheDocument();
    }
  });

  it("shows the label of a waiting action and no result box when the agent has written nothing", async () => {
    api({}, { list: [row({ id: "w", kind: "restart", status: "queued", result: null }), row({ id: "v", kind: "update", status: "delivered", result: null })] });
    const { container } = open();
    expect(await screen.findByText(en.machines.actions.status.queued)).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.status.delivered)).toBeInTheDocument();
    expect(container.querySelector(".action-result")).toBeNull();
  });

  it("renders a result that looks like markup as the literal text, not as markup", async () => {
    api({}, { list: [row({ id: "m", kind: "status", status: "failed", result: "<b>x</b> <img src=x onerror=alert(1)>" })] });
    const { container } = open();
    const box = await waitFor(() => {
      const el = container.querySelector(".action-result");
      expect(el).not.toBeNull();
      return el as HTMLElement;
    });
    expect(box.textContent).toBe("<b>x</b> <img src=x onerror=alert(1)>");
    expect(box.querySelector("b")).toBeNull();
    expect(container.querySelector(".action-result img")).toBeNull();
  });

  it("tells the owner when the machine is offline, and not when it is online", async () => {
    api({}, { view: { online: false } });
    const { unmount } = open();
    await button("status");
    expect(screen.getByText(en.machines.actions.offline)).toBeInTheDocument();
    unmount();
    vi.restoreAllMocks();

    api();
    open();
    await button("status");
    expect(screen.queryByText(en.machines.actions.offline)).toBeNull();
  });

  it("shows a failure to read the list", async () => {
    api({ "GET /agents/a1/actions": { status: 403, body: { title: "forbidden" } } });
    open();
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
  });

  it("reads the list again when the stream says an action changed", async () => {
    let finished = false;
    const calls = api({
      "GET /agents/a1/actions": () => ({ body: finished ? [row({ id: "z", kind: "update_check", status: "succeeded", result: "up to date" })] : [row({ id: "z", kind: "update_check", status: "queued" })] }),
    });
    open();
    expect(await screen.findByText(en.machines.actions.status.queued)).toBeInTheDocument();
    await settle(calls);
    const before = count(calls, "/agents/a1/actions");
    finished = true;
    act(() => FakeEventSource.last!.emit("action", { id: "z", agentId: "a1", status: "succeeded" }));
    expect(await screen.findByText("up to date")).toBeInTheDocument();
    expect(count(calls, "/agents/a1/actions")).toBeGreaterThan(before);
    expect(screen.queryByText(en.machines.actions.status.queued)).toBeNull();
  });

  it("shows every member the daemon's version, how it runs, what it may run and when it last spoke, and the owner the actions below", async () => {
    api({}, { view: { execLevel: "argv", serviceMode: true }, userId: "someone-else", version: "0.6.1" });
    open();
    await screen.findByText(en.machines.actions.ownerOnly);
    expect(screen.getByText(en.machines.actions.version)).toBeInTheDocument();
    expect(screen.getByText("0.6.1")).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.pod)).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.podServer)).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.exec)).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.lastSeen)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.machines.actions.kind.update })).toBeNull();
  });

  it("says desktop when the agent does not run as a service and unknown when the version is not known", async () => {
    api({}, { view: { serviceMode: false }, version: "" });
    open();
    expect(await screen.findByText(en.machines.actions.podDesktop)).toBeInTheDocument();
    expect(screen.getByText(en.machines.actions.versionUnknown)).toBeInTheDocument();
  });
});
