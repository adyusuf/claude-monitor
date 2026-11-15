import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt, type Call } from "../test/helpers";
import { WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const settings = { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 };
const info = { id: "w1", name: "Team", role: "owner", settings };
const row = (id: number, action: string) =>
  ({ id, action, actorUserId: "u1", actorAgentId: null, targetType: null, targetId: null, at: "2026-11-01T10:00:00Z", detail: null });
const page = () => renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
const auditCalls = (calls: Call[]) => calls.filter((c) => c.method === "GET" && c.path.startsWith("/workspaces/w1/audit"));

describe("the audit tab of the workspace settings", () => {
  it("loads the older page from the cursor the server gave, below the first, and offers no more after the last", async () => {
    const calls = mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info },
      "GET /workspaces/w1/audit": (call) => call.path.includes("before=c1")
        ? { body: { items: [row(1, "workspace.created")], next: null } }
        : { body: { items: [row(2, "workspace.settings_changed")], next: "c1" } },
    });
    page();
    await openTab(en.workspace.audit);
    expect(await screen.findByText("workspace.settings_changed")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: en.sessions.more }));

    expect(await screen.findByText("workspace.created")).toBeInTheDocument();
    expect(screen.getByText("workspace.settings_changed")).toBeInTheDocument();
    expect(auditCalls(calls).map((c) => c.path)).toEqual(["/workspaces/w1/audit", "/workspaces/w1/audit?before=c1"]);
    expect(screen.queryByRole("button", { name: en.sessions.more })).toBeNull();
  });

  it("keeps the rows it has and shows why when the older page cannot be read", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info },
      "GET /workspaces/w1/audit": (call) => call.path.includes("before=c1")
        ? { status: 500 }
        : { body: { items: [row(2, "workspace.settings_changed")], next: "c1" } },
    });
    page();
    await openTab(en.workspace.audit);
    await userEvent.click(await screen.findByRole("button", { name: en.sessions.more }));

    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.getByText("workspace.settings_changed")).toBeInTheDocument();
  });

  it("shows why the log could not be read instead of an empty log", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1": { body: info }, "GET /workspaces/w1/audit": { status: 500 } });
    page();
    await openTab(en.workspace.audit);
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.queryByText(en.workspace.noAudit)).toBeNull();
  });

  it("reads the log again after a save, so the change it recorded shows", async () => {
    let saved = false;
    mockApi({
      "GET /me": { body: ME }, "GET /workspaces/w1": { body: info },
      "GET /workspaces/w1/audit": () => ({ body: { items: saved ? [row(3, "workspace.settings_changed")] : [], next: null } }),
      "PUT /workspaces/w1/settings": () => { saved = true; return { status: 204 }; },
    });
    page();
    await userEvent.click(await screen.findByRole("button", { name: en.workspace.save }));
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();

    await openTab(en.workspace.audit);

    await waitFor(() => expect(screen.getByText("workspace.settings_changed")).toBeInTheDocument());
  });
});
