import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt } from "../test/helpers";
import { NewWorkspacePage, WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const settings = { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 };
const info = (over: { role?: string; settings?: object } = {}) => ({ id: "w1", name: "Team", role: over.role ?? "owner", settings: over.settings ?? settings });
const audit = { "GET /workspaces/w1/audit": { body: { items: [], next: null } } };
const page = () => renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
const save = () => userEvent.click(screen.getByRole("button", { name: en.workspace.save }));

describe("saving the workspace settings when the server refuses", () => {
  it("shows the server's error and not the saved notice", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit, "PUT /workspaces/w1/settings": { status: 500 } });
    page();
    await screen.findByLabelText(en.workspace.name);
    await save();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
  });

  it("names a value that is out of range", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit,
      "PUT /workspaces/w1/settings": { status: 400, body: { errors: { retentionDays: ["out_of_range"] } } },
    });
    page();
    await openTab(en.workspace.sectionCapture);
    await userEvent.clear(await screen.findByLabelText(en.workspace.retention));
    await userEvent.type(screen.getByLabelText(en.workspace.retention), "99999");
    await save();
    expect(await screen.findByText(en.errors.out_of_range)).toBeInTheDocument();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
  });

  it("stops at a refused rename: the error shows and the settings are not sent", async () => {
    const calls = mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit,
      "PATCH /workspaces/w1": { status: 409, body: { title: "conflict" } },
      "PUT /workspaces/w1/settings": { status: 204 },
    });
    page();
    await userEvent.clear(await screen.findByLabelText(en.workspace.name));
    await userEvent.type(screen.getByLabelText(en.workspace.name), "Renamed");
    await save();
    expect(await screen.findByText(en.errors.conflict)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });
});

describe("saving a new name when the settings after it fail", () => {
  it("reads the signed-in user again for the new name and does not send the rename a second time", async () => {
    const calls = mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit,
      "PATCH /workspaces/w1": { status: 204 },
      "PUT /workspaces/w1/settings": { status: 500 },
    });
    page();
    await userEvent.clear(await screen.findByLabelText(en.workspace.name));
    await userEvent.type(screen.getByLabelText(en.workspace.name), "Renamed");
    await save();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    const order = calls.map((c) => `${c.method} ${c.path}`);
    await waitFor(() => expect(calls.map((c) => `${c.method} ${c.path}`).slice(order.indexOf("PATCH /workspaces/w1"))).toContain("GET /me"));

    await save();

    await waitFor(() => expect(calls.filter((c) => c.method === "PUT")).toHaveLength(2));
    expect(calls.filter((c) => c.method === "PATCH")).toHaveLength(1);
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
  });
});

describe("loading the workspace settings", () => {
  it("shows only the error when the workspace cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { status: 403 }, ...audit });
    page();
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
    expect(screen.queryByRole("tab")).toBeNull();
  });

  it("still shows the page, with the reason on the audit tab, when only the audit log cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, "GET /workspaces/w1/audit": { status: 500 } });
    page();
    expect(await screen.findByLabelText(en.workspace.name)).toHaveValue("Team");
    await openTab(en.workspace.audit);
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.queryByText(en.workspace.noAudit)).toBeNull();
  });

  it("keeps what was typed on a tab when the person visits another tab and comes back", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit });
    page();
    await userEvent.clear(await screen.findByLabelText(en.workspace.name));
    await userEvent.type(screen.getByLabelText(en.workspace.name), "Half-typed");
    await openTab(en.workspace.sectionCapture);
    await openTab(en.workspace.sectionGeneral);
    expect(screen.getByLabelText(en.workspace.name)).toHaveValue("Half-typed");
  });
});

describe("the access tab of the workspace settings", () => {
  const enforcing = (value: boolean) => info({ settings: { ...settings, enforceAccessPolicies: value } });

  it("keeps an unsaved switch when the person visits another tab and comes back", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: enforcing(false) }, ...audit });
    page();
    await openTab(en.access.enforceTitle);
    await userEvent.click(await screen.findByRole("switch"));
    await openTab(en.workspace.sectionGeneral);
    await openTab(en.access.enforceTitle);
    expect(screen.getByRole("switch")).toBeChecked();
  });

  it("reads the workspace again after a save, so the switch and Save follow what is stored", async () => {
    let enforce = false;
    const calls = mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": () => ({ body: enforcing(enforce) }), ...audit,
      "PUT /workspaces/w1/settings": () => {
        enforce = true;
        return { status: 204 };
      },
    });
    page();
    await openTab(en.access.enforceTitle);
    await userEvent.click(await screen.findByRole("switch"));
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    await waitFor(() => expect(calls.filter((c) => c.method === "GET" && c.path === "/workspaces/w1")).toHaveLength(2));
    await waitFor(() => expect(screen.getByRole("button", { name: en.workspace.save })).toBeDisabled()); // the stored value now equals the switch
  });

  it("shows an error when the read after a save fails", async () => {
    let reads = 0;
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1": () => (++reads === 1 ? { body: enforcing(false) } : { status: 500 }),
      ...audit,
      "PUT /workspaces/w1/settings": { status: 204 },
    });
    page();
    await openTab(en.access.enforceTitle);
    await userEvent.click(await screen.findByRole("switch"));
    await save();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
  });

  it("is read-only when the person is not an owner or admin", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info({ role: "member", settings: { ...settings, enforceAccessPolicies: true } }) }, ...audit });
    page();
    await openTab(en.access.enforceTitle);
    expect(await screen.findByRole("switch")).toBeDisabled();
    expect(screen.queryByRole("button", { name: en.workspace.save })).toBeNull();
  });
});

describe("the remote work tab of the workspace settings", () => {
  it("keeps an unsaved value when the person visits another tab and comes back", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit,
      "GET /workspaces/w1/remote-settings": { body: { remoteRunsEnabled: true, alertCpuPct: 90, alertMemoryPct: 85, alertDiskPct: 80, alertSustainSeconds: 300 } },
    });
    page();
    await openTab(en.remote.settingsTitle);
    await userEvent.clear(await screen.findByLabelText(en.remote.alertCpu));
    await userEvent.type(screen.getByLabelText(en.remote.alertCpu), "70");
    await openTab(en.workspace.sectionGeneral);
    await openTab(en.remote.settingsTitle);
    expect(screen.getByLabelText(en.remote.alertCpu)).toHaveValue(70);
  });
});

describe("creating a workspace when the server refuses", () => {
  it("shows the error and keeps the name typed", async () => {
    mockApi({ "GET /me": { body: ME }, "POST /workspaces": { status: 500 } });
    renderAt("/workspaces/new", [{ path: "/workspaces/new", element: <NewWorkspacePage /> }, { path: "/w/:ws/projects", element: <div>new projects</div> }]);
    await userEvent.type(await screen.findByLabelText(en.workspace.name), "Second");
    await userEvent.click(screen.getByRole("button", { name: en.workspace.create }));
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.getByLabelText(en.workspace.name)).toHaveValue("Second");
    expect(screen.queryByText("new projects")).toBeNull();
  });
});
