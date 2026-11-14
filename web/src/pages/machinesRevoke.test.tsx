import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { Me } from "../api/types";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { DevicePage, MachinesPage } from "./MachinesPage";
import { agent } from "./machinesFixture";

const route = [{ path: "/w/:ws/machines", element: <MachinesPage /> }];
const asRole = (role: Me["workspaces"][number]["role"]): Me => ({ ...ME, workspaces: [{ id: "w1", name: "Team", role }] });
const revokeButtons = () => screen.queryAllByRole("button", { name: "Disconnect" });

describe("who sees the disconnect button on the machines list", () => {
  it("shows a plain member the button for their own machine only", async () => {
    mockApi({
      "GET /me": { body: asRole("member") },
      "GET /workspaces/w1/agents": { body: [agent({ id: "a1", hostname: "others-box", userId: "u2" }), agent({ id: "a2", hostname: "own-box", userId: "u1" })] },
    });
    renderAt("/w/w1/machines", route);
    await screen.findByText("own-box");
    await waitFor(() => expect(revokeButtons()).toHaveLength(1));
    expect(within(screen.getByText("own-box").closest("tr")!).getByRole("button", { name: "Disconnect" })).toBeInTheDocument();
  });

  it("shows a viewer no button for somebody else's machine", async () => {
    const calls = mockApi({
      "GET /me": { body: asRole("viewer") },
      "GET /workspaces/w1/agents": { body: [agent({ userId: "u2" })] },
    });
    renderAt("/w/w1/machines", route);
    await screen.findByText("laptop-1");
    await waitFor(() => expect(calls.some((c) => c.path === "/me")).toBe(true));
    await act(async () => {});
    expect(revokeButtons()).toHaveLength(0);
  });

  it("shows an admin the button for somebody else's machine", async () => {
    mockApi({
      "GET /me": { body: asRole("admin") },
      "GET /workspaces/w1/agents": { body: [agent({ userId: "u2" })] },
    });
    renderAt("/w/w1/machines", route);
    await screen.findByText("laptop-1");
    expect(await screen.findByRole("button", { name: "Disconnect" })).toBeInTheDocument();
  });

  it("shows no button on a machine that is already disconnected, even to the owner of the workspace", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent({ status: "revoked", userId: "u1" })] },
    });
    renderAt("/w/w1/machines", route);
    expect(await screen.findByText("Disconnected")).toBeInTheDocument();
    expect(revokeButtons()).toHaveLength(0);
  });
});

describe("disconnecting a machine", () => {
  it("sends nothing when the person cancels the confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent()] },
      "POST /agents/a1/revoke": { status: 204 },
    });
    renderAt("/w/w1/machines", route);
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(window.confirm).toHaveBeenCalledWith(en.machines.revokeConfirm);
    expect(calls.some((c) => c.path === "/agents/a1/revoke")).toBe(false);
  });

  it("shows the error and keeps the machine listed when the server refuses", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent()] },
      "POST /agents/a1/revoke": { status: 403, body: { title: "forbidden" } },
    });
    renderAt("/w/w1/machines", route);
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Disconnect" })).toBeInTheDocument();
  });
});

describe("approving a device when the server refuses", () => {
  it("shows the error and not the connected message", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /device/lookup/BCDF-GHJK": { body: { userCode: "BCDF-GHJK", hostname: "desk", os: "windows", arch: "x64", agentVersion: "0.3.0", createdAt: "", expiresAt: "" } },
      "POST /device/approve": { status: 409, body: { title: "conflict" } },
    });
    renderAt("/device?code=BCDF-GHJK", [{ path: "/device", element: <DevicePage /> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Connect this machine" }));
    expect(await screen.findByText(en.errors.conflict)).toBeInTheDocument();
    expect(screen.queryByText(/The machine is connected/)).toBeNull();
  });
});
