import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { RemoteSettings } from "../api/types";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { RemoteSettingsCard } from "./RemoteSettingsCard";
import { WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const settings = (over: Partial<RemoteSettings> = {}): RemoteSettings => ({
  remoteRunsEnabled: true, alertCpuPct: 90, alertMemoryPct: 85, alertDiskPct: 80, alertSustainSeconds: 300, ...over,
});
const card = (canEdit: boolean) => renderAt("/w/w1", [{ path: "/w/:ws", element: <RemoteSettingsCard ws="w1" canEdit={canEdit} /> }]);
const field = (label: string) => screen.getByLabelText(label);
const retype = async (label: string, value: string) => {
  await userEvent.clear(field(label));
  await userEvent.type(field(label), value);
};
const save = () => userEvent.click(screen.getByRole("button", { name: "Save" }));

describe("the remote work settings card", () => {
  it("shows the stored values to an admin and lets them change them", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card(true);
    expect(await screen.findByLabelText(en.remote.alertCpu)).toHaveValue(90);
    expect(field(en.remote.alertMemory)).toHaveValue(85);
    expect(field(en.remote.alertDisk)).toHaveValue(80);
    expect(field(en.remote.alertSustain)).toHaveValue(300);
    expect(screen.getByRole("switch")).toBeChecked();
    expect(screen.queryByText(en.remote.settingsReadOnly)).toBeNull();
    await retype(en.remote.alertCpu, "70");
    await retype(en.remote.alertMemory, "60");
    await retype(en.remote.alertDisk, "50");
    await retype(en.remote.alertSustain, "600");
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ remoteRunsEnabled: true, alertCpuPct: 70, alertMemoryPct: 60, alertDiskPct: 50, alertSustainSeconds: 600 });
  });

  it("is read-only for a non-admin: disabled fields, no Save, an explanation", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() } });
    card(false);
    expect(await screen.findByText(en.remote.settingsReadOnly)).toBeInTheDocument();
    for (const label of [en.remote.alertCpu, en.remote.alertMemory, en.remote.alertDisk, en.remote.alertSustain]) expect(field(label)).toBeDisabled();
    expect(screen.getByRole("switch")).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
  });

  it.each([
    ["CPU 0", en.remote.alertCpu, "0"], ["CPU 101", en.remote.alertCpu, "101"], ["memory 0", en.remote.alertMemory, "0"],
    ["disk 101", en.remote.alertDisk, "101"], ["a fractional percentage", en.remote.alertDisk, "50.5"],
    ["sustain 59", en.remote.alertSustain, "59"], ["sustain 86401", en.remote.alertSustain, "86401"],
  ])("refuses %s on the client without calling the API", async (_name, label, value) => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() } });
    card(true);
    await screen.findByLabelText(label);
    await retype(label, value);
    await save();
    expect(await screen.findByText(en.errors.out_of_range)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it.each([
    ["CPU 1", en.remote.alertCpu, "1"], ["CPU 100", en.remote.alertCpu, "100"], ["sustain 60", en.remote.alertSustain, "60"],
    ["sustain 86400", en.remote.alertSustain, "86400"],
  ])("accepts %s, the edge of the range", async (_name, label, value) => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card(true);
    await screen.findByLabelText(label);
    await retype(label, value);
    await save();
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(screen.queryByText(en.errors.out_of_range)).toBeNull();
  });

  it("asks before switching remote work off, and does not save when declined", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() } });
    card(true);
    await userEvent.click(await screen.findByRole("switch"));
    await save();
    expect(confirm).toHaveBeenCalledWith(en.remote.disableConfirm);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("saves the switch off once confirmed", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card(true);
    await userEvent.click(await screen.findByRole("switch"));
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ remoteRunsEnabled: false });
  });

  it("does not ask when the switch was already off or is being turned on", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings({ remoteRunsEnabled: false }) }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card(true);
    await userEvent.click(await screen.findByRole("switch"));
    await save();
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(confirm).not.toHaveBeenCalled();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ remoteRunsEnabled: true });
  });

  it("does not ask when remote work was already off and only a threshold changes", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings({ remoteRunsEnabled: false }) }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card(true);
    await screen.findByLabelText(en.remote.alertCpu);
    await retype(en.remote.alertCpu, "70");
    await save();
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(confirm).not.toHaveBeenCalled();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ remoteRunsEnabled: false, alertCpuPct: 70 });
  });

  it("compares with what was last saved: off then on (no question), then off again asks once more", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card(true);
    await userEvent.click(await screen.findByRole("switch"));
    await save();
    await waitFor(() => expect(calls.filter((c) => c.method === "PUT")).toHaveLength(1));
    expect(confirm).toHaveBeenCalledTimes(1);
    await userEvent.click(screen.getByRole("switch"));
    await save();
    await waitFor(() => expect(calls.filter((c) => c.method === "PUT")).toHaveLength(2));
    expect(confirm).toHaveBeenCalledTimes(1);
    await userEvent.click(screen.getByRole("switch"));
    await save();
    await waitFor(() => expect(calls.filter((c) => c.method === "PUT")).toHaveLength(3));
    expect(confirm).toHaveBeenCalledTimes(2);
  });

  it("shows the API's failure on save and keeps the form", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": { status: 403 } });
    card(true);
    await screen.findByLabelText(en.remote.alertCpu);
    await retype(en.remote.alertCpu, "70");
    await save();
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
    expect(field(en.remote.alertCpu)).toHaveValue(70);
  });

  it("is absent when the API has no remote work (404)", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { status: 404 } });
    const { container } = card(true);
    await waitFor(() => expect(calls.some((c) => c.path === "/workspaces/w1/remote-settings")).toBe(true));
    await waitFor(() => expect(calls.some((c) => c.path === "/me")).toBe(true));
    expect(screen.queryByText(en.remote.settingsTitle)).toBeNull();
    expect(container.querySelector(".card")).toBeNull();
  });

  it("shows a titled error, not a form, when the read fails for another reason", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { status: 403 } });
    card(true);
    expect(await screen.findByText(en.remote.settingsTitle)).toBeInTheDocument();
    expect(screen.getByText(en.errors.forbidden)).toBeInTheDocument();
    expect(screen.queryByRole("switch")).toBeNull();
  });
});

describe("the workspace settings page", () => {
  const info = (role: string) => ({ id: "w1", name: "Team", role, settings: { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 } });
  const page = () => renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);

  it("carries the remote work card, editable for an owner or an admin", async () => {
    for (const role of ["owner", "admin"]) {
      mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info(role) }, "GET /workspaces/w1/audit": { body: { items: [], next: null } }, "GET /workspaces/w1/remote-settings": { body: settings() } });
      const { unmount } = page();
      expect(await screen.findByText(en.remote.settingsTitle)).toBeInTheDocument();
      expect(await screen.findByLabelText(en.remote.alertCpu)).toBeEnabled();
      unmount();
    }
  });

  it("shows the card read-only to any other role", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info("member") }, "GET /workspaces/w1/audit": { body: { items: [], next: null } }, "GET /workspaces/w1/remote-settings": { body: settings() } });
    page();
    expect(await screen.findByLabelText(en.remote.alertCpu)).toBeDisabled();
  });
});
