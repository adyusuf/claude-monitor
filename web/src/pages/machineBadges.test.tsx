import { act, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { MachinesPage } from "./MachinesPage";
import { agent, count, settle } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const listRoute = [{ path: "/w/:ws/machines", element: <MachinesPage /> }];
const pageRoute = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const rowOf = async (host: string) => (await screen.findByRole("link", { name: host })).closest("tr")!;

describe("the role and Claude badges on the machines list", () => {
  const list = (views: ReturnType<typeof machine>[]) => mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/agents": { body: views.map((v) => agent({ id: v.agentId, hostname: v.hostname })) },
    "GET /workspaces/w1/machines": { body: views },
  });

  it("shows the effective role in red for production or a database, plain for the rest, and the machine's claim in the title", async () => {
    list([
      machine({ agentId: "p", hostname: "prod-box", effectiveEnvironment: "production", effectiveFunction: "database" }),
      machine({ agentId: "c", hostname: "claim-box", effectiveEnvironment: "production", effectiveFunction: null, claimedEnvironment: "production" }),
      machine({ agentId: "d", hostname: "dev-box", effectiveEnvironment: "dev", effectiveFunction: "development" }),
    ]);
    renderAt("/w/w1/machines", listRoute);
    expect(within(await rowOf("prod-box")).getByText("production/database")).toHaveClass("badge", "status-failed");
    const claimed = within(await rowOf("claim-box")).getByText("production/not set");
    expect(claimed).toHaveClass("status-failed");
    expect(claimed).toHaveAttribute("title", "The machine itself says: production/not set.");
    const quiet = within(await rowOf("dev-box")).getByText("dev/development");
    expect(quiet).toHaveClass("badge");
    expect(quiet).not.toHaveClass("status-failed");
    expect(quiet).toHaveAttribute("title", "");
  });

  it("shows a database as red even in a test environment, and a machine with no role as muted 'not set' without a badge class", async () => {
    list([
      machine({ agentId: "d", hostname: "db-box", effectiveEnvironment: "test", effectiveFunction: "database" }),
      machine({ agentId: "n", hostname: "bare-box" }),
    ]);
    renderAt("/w/w1/machines", listRoute);
    expect(within(await rowOf("db-box")).getByText("test/database")).toHaveClass("status-failed");
    const unset = within(await rowOf("bare-box")).getByText(en.machines.role.notSet);
    expect(unset).toHaveClass("muted");
    expect(unset).not.toHaveClass("badge");
    expect(unset).not.toHaveClass("status-failed");
  });

  it("says unknown for an older agent (null or absent kind), 'not installed' for none, and the version with idle sessions for an install", async () => {
    list([
      machine({ agentId: "a", hostname: "old-box", claudeKind: null }),
      machine({ agentId: "b", hostname: "older-box" }),
      machine({ agentId: "c", hostname: "empty-box", claudeKind: "none" }),
      machine({ agentId: "d", hostname: "native-box", claudeKind: "native", claudeVersion: "2.1.300", claudeSessionsIdle: 1 }),
      machine({ agentId: "e", hostname: "npm-box", claudeKind: "npm", claudeVersion: "2.0.1", claudeSessionsIdle: null }),
    ]);
    renderAt("/w/w1/machines", listRoute);
    expect(within(await rowOf("old-box")).getByText("unknown (older agent)")).toHaveClass("muted");
    expect(within(await rowOf("older-box")).getByText("unknown (older agent)")).toBeInTheDocument();
    expect(within(await rowOf("empty-box")).getByText("not installed")).toHaveClass("muted");
    const native = within(await rowOf("native-box")).getByText("Claude Code 2.1.300 · 1 idle");
    expect(native).toHaveClass("badge");
    expect(native).toHaveAttribute("title", en.machines.claude.kind.native);
    const npm = within(await rowOf("npm-box")).getByText("Claude Code 2.0.1");
    expect(npm).toHaveAttribute("title", en.machines.claude.kind.npm);
  });

  it("reads the machines again on a machine message and ignores a grant message", async () => {
    const calls = list([machine()]);
    renderAt("/w/w1/machines", listRoute);
    await rowOf("laptop-1");
    expect(count(calls, "/workspaces/w1/machines")).toBe(1);
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    act(() => FakeEventSource.last!.emit("grant", {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs * 3));
    expect(count(calls, "/workspaces/w1/machines")).toBe(1);
    act(() => FakeEventSource.last!.emit("machine", {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
    expect(count(calls, "/workspaces/w1/machines")).toBe(2);
  });
});

describe("the tool inventory on the machine page", () => {
  const page = (over: Partial<ReturnType<typeof machine>>) => mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine(over)] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
  });

  it("says an older agent has not reported tools, and an empty list found none", async () => {
    page({ tools: null });
    const { unmount } = renderAt("/w/w1/machines/a1", pageRoute);
    expect(await screen.findByText(en.machines.tools.unknown)).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: en.machines.tools.title })).toBeNull();
    unmount();
    page({ tools: [] });
    renderAt("/w/w1/machines/a1", pageRoute);
    expect(await screen.findByText(en.machines.tools.none)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.tools.unknown)).toBeNull();
  });

  it("lists each tool with its version, marks an untrusted one and a script interpreter", async () => {
    const node = { name: "node", path: "usr/local/bin/node", version: "22.1.0", trusted: false };
    const bash = { name: "bash", path: "bin/bash", version: "5.2", trusted: true };
    page({ tools: [node, bash, { name: "tmux", path: "bin/tmux", version: null, trusted: true }], interpreters: [bash] });
    renderAt("/w/w1/machines/a1", pageRoute);
    const chips = within(await screen.findByRole("list", { name: en.machines.tools.title })).getAllByRole("listitem");
    expect(chips.map((c) => c.textContent)).toEqual(["node 22.1.0", "bash 5.2 ✓", "tmux"]);
    expect(chips[0]).toHaveClass("badge", "warn");
    expect(chips[0]).toHaveAttribute("title", `usr/local/bin/node — ${en.machines.tools.untrusted}`);
    expect(chips[1]).toHaveClass("badge");
    expect(chips[1]).not.toHaveClass("warn");
    expect(chips[1]).toHaveAttribute("title", "bin/bash");
    expect(chips[2]).not.toHaveClass("warn");
    expect(screen.getByText(en.machines.tools.hint)).toBeInTheDocument();
  });

  it("reads the machine again on a machine message", async () => {
    const calls = page({});
    renderAt("/w/w1/machines/a1", pageRoute);
    await screen.findByRole("heading", { level: 1 });
    await settle(calls);
    const base = count(calls, "/workspaces/w1/machines");
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    act(() => FakeEventSource.last!.emit("machine", {}));
    await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
    expect(count(calls, "/workspaces/w1/machines")).toBe(base + 1);
  });
});
