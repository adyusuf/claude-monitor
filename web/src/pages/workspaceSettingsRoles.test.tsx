import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt } from "../test/helpers";
import { WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const settings = { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144, agentUpdate: "off", claudeUpdate: false };
const pageAs = async (role: string) => {
  mockApi({
    "GET /me": { body: ME }, "GET /workspaces/w1": { body: { id: "w1", name: "Team", role, settings } },
    "GET /workspaces/w1/audit": { body: { items: [], next: null } },
  });
  renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
  await screen.findByLabelText(en.workspace.name);
};
// The name, capture and update controls, one tab at a time.
const controls = async () => {
  const general = [screen.getByLabelText(en.workspace.name)];
  await openTab(en.workspace.sectionCapture);
  const capture = [
    screen.getByRole("checkbox", { name: new RegExp(en.workspace.maskSecrets) }),
    screen.getByLabelText(en.workspace.retention), screen.getByLabelText(en.workspace.eventMax),
  ];
  await openTab(en.workspace.sectionUpdates);
  const updates = [screen.getByLabelText(en.workspace.agentUpdate), screen.getByRole("checkbox", { name: new RegExp(en.workspace.claudeUpdate) })];
  return [...general, ...capture, ...updates];
};

describe("the general, capture and update tabs by role", () => {
  it.each(["member", "viewer"])("are read-only for a %s: disabled fields, an explanation and no Save", async (role) => {
    await pageAs(role);
    for (const control of await controls()) expect(control).toBeDisabled();
    expect(screen.queryByRole("button", { name: en.workspace.save })).toBeNull();
    expect(screen.getAllByText(en.remote.settingsReadOnly).length).toBeGreaterThan(0);
  });

  it.each(["owner", "admin"])("can be changed and saved by an %s", async (role) => {
    await pageAs(role);
    for (const control of await controls()) expect(control).toBeEnabled();
    expect(screen.getAllByRole("button", { name: en.workspace.save }).length).toBeGreaterThan(0);
  });
});
