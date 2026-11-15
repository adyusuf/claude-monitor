import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { AgentRow } from "../api/types";
import { ME, mockApi, renderAt } from "../test/helpers";
import { DevicePage, MachinesPage } from "./MachinesPage";

const agent = (over: Partial<AgentRow> = {}): AgentRow => ({
  id: "a1", machineId: "m1", hostname: "laptop-1", os: "macos", arch: "arm64", version: "0.3.0", status: "active",
  userId: "u1", userName: "Örnek Kişi", enrolledAt: new Date().toISOString(), lastHeartbeatAt: null, revokedAt: null, ...over,
});

const MACHINES = [{ path: "/w/:ws/machines", element: <MachinesPage /> }];

describe("the machines list when a load fails", () => {
  it("shows only the error, never the spinner beside it, when the first load fails", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/agents": { status: 500 } });
    renderAt("/w/w1/machines", MACHINES);
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(screen.queryByRole("status", { name: "Loading…" })).toBeNull();
  });

  it("drops the error once a later action succeeds", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let revokes = 0;
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/agents": { body: [agent()] },
      "POST /agents/a1/revoke": () => (++revokes === 1 ? { status: 500 } : { status: 204 }),
    });
    renderAt("/w/w1/machines", MACHINES);
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
  });
});

describe("the device page for a user with no workspace to contribute to", () => {
  it("says why and links to creating a workspace", async () => {
    mockApi({
      "GET /me": { body: { ...ME, workspaces: [{ id: "w2", name: "Other", role: "viewer" }] } },
      "GET /device/lookup/BCDF-GHJK": { body: { userCode: "BCDF-GHJK", hostname: "desk", os: "macos", arch: "arm64", agentVersion: "0.3.0", createdAt: "", expiresAt: "" } },
    });
    renderAt("/device?code=BCDF-GHJK", [{ path: "/device", element: <DevicePage /> }]);
    const link = await screen.findByRole("link", { name: "Create a workspace" });
    expect(link).toHaveAttribute("href", "/workspaces/new");
  });
});
