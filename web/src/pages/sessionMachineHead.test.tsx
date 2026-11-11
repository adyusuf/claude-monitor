import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { MachineDetail, MachineView } from "../api/types";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { ClaudeState, SessionMachineHead } from "./SessionMachineHead";

const state = (over: Partial<MachineView>) => {
  renderAt("/x", [{ path: "/x", element: <ClaudeState view={machine({ claudeVersion: "2.1", ...over })} /> }]);
  return screen.getByText(/Claude/);
};

describe("whether Claude Code is on a machine and running there", () => {
  it.each<[string, Partial<MachineView>, string]>([
    ["is not reported by an older agent (no field)", { claudeKind: undefined }, "Claude: not reported"],
    ["is not reported by an agent that sent null", { claudeKind: null }, "Claude: not reported"],
    ["is not installed", { claudeKind: "none" }, "Claude not installed"],
    ["is installed, with no word on whether it runs, when the agent gave no session count", { claudeKind: "native" }, "Claude 2.1"],
    ["is installed, and not running, when the machine is online and no session lives", { claudeKind: "native", online: true, claudeSessionsLive: 0 }, "Claude 2.1 · not running"],
    ["is running with the session count when the machine is online and sessions live", { claudeKind: "native", online: true, claudeSessionsLive: 3 }, "Claude 2.1 · running, 3 session(s)"],
    ["is only installed, not running, for an offline machine whose last report is old news", { claudeKind: "native", online: false, claudeSessionsLive: 3 }, "Claude 2.1"],
  ])("Claude %s", (_name, over, expected) => {
    expect(state(over)).toHaveTextContent(new RegExp(`^${expected.replace(/[().]/g, "\\$&")}$`));
  });

  it("marks a running Claude as good and the others as plain", () => {
    expect(state({ claudeKind: "native", online: true, claudeSessionsLive: 1 })).toHaveClass("ok");
  });

  it("does not mark a stopped Claude as good", () => {
    expect(state({ claudeKind: "native", online: true, claudeSessionsLive: 0 })).not.toHaveClass("ok");
  });
});

describe("the head of a machine's card on the sessions page", () => {
  const head = (view: MachineView | undefined, over: { owned?: boolean; count?: number } = {}) => renderAt("/x", [{
    path: "/x", element: <SessionMachineHead ws="w1" hostname="laptop-1" view={view} owned={over.owned ?? false} agentVersion="0.6.0" count={over.count ?? 4} onChanged={() => undefined} />,
  }]);

  it("shows only the name and says the machine is no longer connected when the page does not know it", () => {
    head(undefined);
    expect(screen.getByRole("heading", { level: 2 })).toHaveTextContent("laptop-1");
    expect(screen.queryByRole("link", { name: "laptop-1" })).toBeNull();
    expect(screen.getByText("This machine is no longer connected.")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("links the name to the machine, shows its owner and how many sessions it has, and whether Claude can be started from the web", () => {
    head(machine({ agentId: "a1", userName: "Diğer Kişi", claudeKind: "native", acceptsClaudeControl: true }), { count: 4 });
    expect(screen.getByRole("link", { name: "laptop-1" })).toHaveAttribute("href", "/w/w1/machines/a1");
    expect(screen.getByText("4")).toHaveAttribute("title", "4 session(s)");
    expect(screen.getByText("Diğer Kişi")).toBeInTheDocument();
    expect(screen.getByText("Can be started from the web")).toBeInTheDocument();
  });

  it("leaves out the web-start badge when the machine does not accept it", () => {
    head(machine({ claudeKind: "native", acceptsClaudeControl: false }));
    expect(screen.queryByText("Can be started from the web")).toBeNull();
  });

  it("offers the Claude controls only to the machine's owner, and only where Claude is installed", () => {
    const { unmount } = head(machine({ claudeKind: "native" }), { owned: true });
    expect(screen.getByRole("button", { name: "Start or stop Claude" })).toBeInTheDocument();
    unmount();
    const other = head(machine({ claudeKind: "native" }), { owned: false });
    expect(screen.queryByRole("button", { name: "Start or stop Claude" })).toBeNull();
    other.unmount();
    head(machine({ claudeKind: "none" }), { owned: true });
    expect(screen.queryByRole("button", { name: "Start or stop Claude" })).toBeNull();
  });

  it("opens the machine's Claude dialog, reads the machine's detail and shows its controls", async () => {
    const detail: MachineDetail = { machine: machine({ agentId: "a1", claudeKind: "native" }), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [] };
    const calls = mockApi({ "GET /me": { body: ME }, "GET /agents/a1/detail": { body: detail }, "GET /agents/a1/actions": { body: [] } });
    head(machine({ agentId: "a1", claudeKind: "native" }), { owned: true });
    await userEvent.click(screen.getByRole("button", { name: "Start or stop Claude" }));
    const dialog = await screen.findByRole("dialog", { name: "Claude on laptop-1" });
    expect(await within(dialog).findByText(en.machines.claudeControl.off)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "GET" && c.path === "/agents/a1/detail")).toBe(true);
  });

  it("shows the error inside the dialog when the machine's detail cannot be read", async () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    mockApi({ "GET /me": { body: ME }, "GET /agents/a1/detail": { status: 500, body: {} } });
    head(machine({ agentId: "a1", claudeKind: "native" }), { owned: true });
    await userEvent.click(screen.getByRole("button", { name: "Start or stop Claude" }));
    const dialog = await screen.findByRole("dialog", { name: "Claude on laptop-1" });
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
  });

  it("closes the dialog with its close button", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /agents/a1/detail": { status: 500, body: {} } });
    head(machine({ agentId: "a1", claudeKind: "native" }), { owned: true });
    await userEvent.click(screen.getByRole("button", { name: "Start or stop Claude" }));
    const dialog = await screen.findByRole("dialog", { name: "Claude on laptop-1" });
    await userEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
