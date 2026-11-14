import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AccessEnforcementCard } from "./AccessEnforcementCard";

const card = (over: { enforce?: boolean; canEdit?: boolean; onChanged?: () => void } = {}) => renderAt("/w/w1", [{
  path: "/w/:ws",
  element: <AccessEnforcementCard ws="w1" enforce={over.enforce ?? false} canEdit={over.canEdit ?? true} onChanged={over.onChanged ?? (() => undefined)} />,
}]);
const toggle = () => userEvent.click(screen.getByRole("switch"));
const save = () => userEvent.click(screen.getByRole("button", { name: en.workspace.save }));
const reauth = { status: 403, body: { title: "reauth_required" } };
const settingsCalls = (calls: { method: string; path: string; body: unknown }[]) => calls.filter((c) => c.method === "PUT" && c.path === "/workspaces/w1/settings");

describe("the access enforcement card", () => {
  it("shows the switch as the workspace has it", () => {
    mockApi({ "GET /me": { body: ME } });
    card({ enforce: true });
    expect(screen.getByRole("switch")).toBeChecked();
  });

  it("is read-only for a non-admin: a disabled switch, an explanation and no Save", () => {
    mockApi({ "GET /me": { body: ME } });
    card({ canEdit: false });
    expect(screen.getByRole("switch")).toBeDisabled();
    expect(screen.getByText(en.remote.settingsReadOnly)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.workspace.save })).toBeNull();
  });

  it("keeps Save disabled until the switch differs from what is stored, and again when it is put back", async () => {
    mockApi({ "GET /me": { body: ME } });
    card({ enforce: false });
    expect(screen.getByRole("button", { name: en.workspace.save })).toBeDisabled();
    await toggle();
    expect(screen.getByRole("button", { name: en.workspace.save })).toBeEnabled();
    await toggle();
    expect(screen.getByRole("button", { name: en.workspace.save })).toBeDisabled();
  });

  it("saves the new value without a code when the API accepts it, and tells the page to read again", async () => {
    const onChanged = vi.fn();
    const calls = mockApi({ "GET /me": { body: ME }, "PUT /workspaces/w1/settings": { status: 204 } });
    card({ onChanged });
    await toggle();
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(settingsCalls(calls).map((c) => c.body)).toEqual([{ enforceAccessPolicies: true }]);
    expect(onChanged).toHaveBeenCalledTimes(1);
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });

  it("switches enforcement off by sending false", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "PUT /workspaces/w1/settings": { status: 204 } });
    card({ enforce: true });
    await toggle();
    await save();
    await screen.findByText(en.workspace.saved);
    expect(settingsCalls(calls).map((c) => c.body)).toEqual([{ enforceAccessPolicies: false }]);
  });

  it("asks for the authenticator code when the sign-in is not recent, and holds Save until one is typed", async () => {
    const onChanged = vi.fn();
    mockApi({ "GET /me": { body: ME }, "PUT /workspaces/w1/settings": reauth });
    card({ onChanged });
    await toggle();
    await save();
    expect(await screen.findByLabelText(en.remote.codeLabel)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.workspace.save })).toBeDisabled();
    expect(screen.queryByText(en.errors.invalid_code)).toBeNull();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("sends the typed code, trimmed, with the same value and then reports it saved", async () => {
    const onChanged = vi.fn();
    const calls = mockApi({
      "GET /me": { body: ME },
      "PUT /workspaces/w1/settings": (c) => ((c.body as { code?: string }).code === "123456" ? { status: 204 } : reauth),
    });
    card({ onChanged });
    await toggle();
    await save();
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), " 123456 ");
    await save();
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(settingsCalls(calls).map((c) => c.body)).toEqual([{ enforceAccessPolicies: true }, { enforceAccessPolicies: true, code: "123456" }]);
    expect(onChanged).toHaveBeenCalledTimes(1);
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });

  it("reports a wrong code as such and keeps asking", async () => {
    const onChanged = vi.fn();
    mockApi({ "GET /me": { body: ME }, "PUT /workspaces/w1/settings": reauth });
    card({ onChanged });
    await toggle();
    await save();
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "000000");
    await save();
    expect(await screen.findByText(en.errors.invalid_code)).toBeInTheDocument();
    expect(screen.getByLabelText(en.remote.codeLabel)).toBeInTheDocument();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("shows any other failure as it is and asks for no code", async () => {
    mockApi({ "GET /me": { body: ME }, "PUT /workspaces/w1/settings": { status: 500 } });
    card();
    await toggle();
    await save();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
  });

  it("takes the saved notice away once the switch is touched again", async () => {
    mockApi({ "GET /me": { body: ME }, "PUT /workspaces/w1/settings": { status: 204 } });
    card();
    await toggle();
    await save();
    await screen.findByText(en.workspace.saved);
    await toggle();
    expect(screen.queryByText(en.workspace.saved)).toBeNull();
  });
});
