import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt } from "../test/helpers";
import { WorkspaceSettingsPage } from "./WorkspaceSettingsPage";

const settings = { maskSecrets: true, retentionDays: 90, eventMaxBytes: 262144 };
const base = {
  "GET /me": { body: ME }, "GET /workspaces/w1": { body: { id: "w1", name: "Team", role: "owner", settings } },
  "GET /workspaces/w1/audit": { body: { items: [], next: null } },
};
const page = async () => {
  renderAt("/w/w1/settings", [{ path: "/w/:ws/settings", element: <WorkspaceSettingsPage /> }]);
  await openTab(en.workspace.sectionCapture);
};
const retentionTo = async (days: string) => {
  await userEvent.clear(await screen.findByLabelText(en.workspace.retention));
  await userEvent.type(screen.getByLabelText(en.workspace.retention), days);
};
const save = () => userEvent.click(screen.getByRole("button", { name: en.workspace.save }));

describe("lowering the retention or turning masking off (F-219)", () => {
  it("asks first, naming what is deleted, and sends nothing when the person declines", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ ...base, "PUT /workspaces/w1/settings": { status: 204 } });
    await page();
    await retentionTo("30");
    await save();
    expect(confirm).toHaveBeenCalledTimes(1);
    const text = String(confirm.mock.calls[0]?.[0]);
    expect(text).toContain("30");
    expect(text).toContain("90");
    for (const what of ["finished runs with their output", "asks", "actions", "resolved alerts", "unused approval rules"]) expect(text).toContain(what);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("sends the lowered value once the person confirms", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({ ...base, "PUT /workspaces/w1/settings": { status: 204 } });
    await page();
    await retentionTo("30");
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ retentionDays: 30 });
  });

  it("asks before masking is turned off and sends nothing when declined", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ ...base, "PUT /workspaces/w1/settings": { status: 204 } });
    await page();
    await userEvent.click(await screen.findByLabelText(new RegExp(en.workspace.maskSecrets)));
    await save();
    expect(confirm).toHaveBeenCalledWith(en.workspace.maskOffConfirm);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("does not ask when the retention is raised or the settings do not lose anything", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ ...base, "PUT /workspaces/w1/settings": { status: 204 } });
    await page();
    await retentionTo("120");
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(confirm).not.toHaveBeenCalled();
    expect(calls.filter((c) => c.method === "PUT")).toHaveLength(1);
  });

  it("asks for the code when the sign-in is not recent and sends it with the next try", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let attempts = 0;
    const calls = mockApi({
      ...base,
      "PUT /workspaces/w1/settings": () => (++attempts === 1 ? { status: 403, body: { title: "reauth_required" } } : { status: 204 }),
    });
    await page();
    await retentionTo("30");
    await save();
    const field = await screen.findByLabelText(en.remote.codeLabel);
    await userEvent.type(field, "123456");
    await save();
    await waitFor(() => expect(calls.filter((c) => c.method === "PUT")).toHaveLength(2));
    expect(calls.filter((c) => c.method === "PUT")[1]?.body).toMatchObject({ retentionDays: 30, code: "123456" });
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
  });
});
