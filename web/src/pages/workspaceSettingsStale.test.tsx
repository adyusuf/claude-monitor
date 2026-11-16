import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt } from "../test/helpers";
import { WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const T0 = "2026-11-16T07:00:00.123456+00:00";
const T1 = "2026-11-16T07:05:00.654321+00:00";
const settings = (updatedAt?: string) => ({ maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144, ...(updatedAt ? { updatedAt } : {}) });
const info = (updatedAt?: string) => ({ id: "w1", name: "Team", role: "owner", settings: settings(updatedAt) });
const remote = (updatedAt?: string) => ({ remoteRunsEnabled: true, alertCpuPct: 90, alertMemoryPct: 85, alertDiskPct: 80, alertSustainSeconds: 300, ...(updatedAt ? { updatedAt } : {}) });
const audit = { "GET /workspaces/w1/audit": { body: { items: [], next: null } } };
const page = () => renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
const save = () => userEvent.click(screen.getByRole("button", { name: en.workspace.save }));

describe("settings saved from a page that is out of date (F-049)", () => {
  it("sends the time the page read with the save", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info(T0) }, ...audit, "PUT /workspaces/w1/settings": { status: 204 } });
    page();
    await screen.findByLabelText(en.workspace.name);
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ expectedUpdatedAt: T0 });
  });

  it("shows the reload message on a 409 stale_update and not the saved notice", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info(T0) }, ...audit,
      "PUT /workspaces/w1/settings": { status: 409, body: { title: "stale_update" } },
    });
    page();
    await screen.findByLabelText(en.workspace.name);
    await save();
    expect(await screen.findByText(en.errors.stale_update)).toBeInTheDocument();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
  });

  it("reads the workspace again after a save, so the next save carries the new time", async () => {
    let stamp = T0;
    const calls = mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": () => ({ body: info(stamp) }), ...audit,
      "PUT /workspaces/w1/settings": () => {
        stamp = T1;
        return { status: 204 };
      },
    });
    page();
    await screen.findByLabelText(en.workspace.name);
    await save();
    await screen.findByText(en.workspace.saved);
    await save();
    await waitFor(() => expect(calls.filter((c) => c.method === "PUT")).toHaveLength(2));
    expect(calls.filter((c) => c.method === "PUT").map((c) => (c.body as { expectedUpdatedAt?: string }).expectedUpdatedAt)).toEqual([T0, T1]);
  });

  it("sends no time to a server that does not hand one out", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info() }, ...audit, "PUT /workspaces/w1/settings": { status: 204 } });
    page();
    await screen.findByLabelText(en.workspace.name);
    await save();
    await screen.findByText(en.workspace.saved);
    expect(calls.find((c) => c.method === "PUT")?.body).not.toHaveProperty("expectedUpdatedAt");
  });
});

describe("the remote work card saved from a page that is out of date (F-049)", () => {
  it("sends the time the card read, shows the reload message on 409, and the page reads the workspace again after a save", async () => {
    let refused = true;
    const calls = mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info(T0) }, ...audit,
      "GET /workspaces/w1/remote-settings": { body: remote(T0) },
      "PUT /workspaces/w1/remote-settings": () => {
        const answer = refused ? { status: 409, body: { title: "stale_update" } } : { body: remote(T1) };
        refused = false;
        return answer;
      },
    });
    page();
    await openTab(en.remote.settingsTitle);
    await screen.findByLabelText(en.remote.alertCpu);
    await save();
    expect(await screen.findByText(en.errors.stale_update)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ expectedUpdatedAt: T0, remoteRunsEnabled: true });

    await save();

    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    const puts = calls.filter((c) => c.method === "PUT");
    expect(puts).toHaveLength(2);
    expect(puts[1]?.body).toMatchObject({ expectedUpdatedAt: T0 }); // the card is still on the time it read until the server answers
    await waitFor(() => expect(calls.filter((c) => c.method === "GET" && c.path === "/workspaces/w1")).toHaveLength(2));
  });
});
