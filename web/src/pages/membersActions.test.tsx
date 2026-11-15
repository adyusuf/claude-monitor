import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt } from "../test/helpers";
import { meAs } from "../test/projects-fixtures";
import { MembersPage } from "./MembersPage";

const joined = "2026-01-01T00:00:00Z";
const members = [
  { userId: "u1", displayName: "Örnek Kişi", email: "ayse+test@gmail.com", role: "owner", joinedAt: joined },
  { userId: "u2", displayName: "Diğer Kişi", email: "deniz+test@gmail.com", role: "member", joinedAt: joined },
];
const route = [{ path: "/w/:ws/members", element: <MembersPage /> }, { path: "/", element: <div>home page</div> }];
const base = (me = ME) => ({ "GET /me": { body: me }, "GET /workspaces/w1/members": { body: members }, "GET /workspaces/w1/invitations": { body: [] } });
const openInvite = async () => {
  await openTab(/Invitations/);
  await userEvent.click(screen.getByRole("button", { name: en.members.invite }));
  return screen.getByRole("dialog");
};

describe("members: a refused list", () => {
  it("shows the error instead of loading forever when the members cannot be read", async () => {
    mockApi({ ...base(), "GET /workspaces/w1/members": { status: 403, body: { title: "forbidden" } } });
    renderAt("/w/w1/members", route);
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: en.common.loading })).toBeNull();
  });
});

describe("members: refused actions and cancelled confirmations", () => {
  it("shows the error when a role change is refused", async () => {
    mockApi({ ...base(), "PATCH /workspaces/w1/members/u2": { status: 403, body: { title: "forbidden" } } });
    renderAt("/w/w1/members", route);
    await userEvent.selectOptions(await screen.findByLabelText(en.members.role), "admin");
    expect(await screen.findByText(en.errors.forbidden)).toBeInTheDocument();
  });

  it("removes nobody when the person cancels the confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ ...base(), "DELETE /workspaces/w1/members/u2": { status: 204 } });
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: en.members.remove }));
    expect(window.confirm).toHaveBeenCalledWith(en.members.removeConfirm);
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
  });

  it("does not leave the workspace when the person cancels the confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ ...base(), "DELETE /workspaces/w1/members/u1": { status: 204 } });
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: en.members.leave }));
    expect(window.confirm).toHaveBeenCalledWith(en.members.leaveConfirm);
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    expect(screen.queryByText("home page")).toBeNull();
  });

  it("stays on the page and shows why when the last owner tries to leave", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockApi({ ...base(), "DELETE /workspaces/w1/members/u1": { status: 400, body: { errors: { userId: ["last_owner"] } } } });
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: en.members.leave }));
    expect(await screen.findByText(en.errors.last_owner)).toBeInTheDocument();
    expect(screen.queryByText("home page")).toBeNull();
  });

  it("reads the signed-in user again and goes home after the person leaves", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = mockApi({ ...base(), "DELETE /workspaces/w1/members/u1": { status: 204 } });
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: en.members.leave }));
    expect(await screen.findByText("home page")).toBeInTheDocument();
    const order = calls.map((c) => `${c.method} ${c.path}`);
    expect(order.slice(order.indexOf("DELETE /workspaces/w1/members/u1"))).toContain("GET /me");
  });
});

describe("members: the invitation drawer", () => {
  it("sends the role the person picked", async () => {
    const calls = mockApi({ ...base(), "POST /workspaces/w1/invitations": { status: 201, body: { id: "i1" } } });
    renderAt("/w/w1/members", route);
    await screen.findByText("Diğer Kişi");
    const drawer = await openInvite();
    await userEvent.type(within(drawer).getByLabelText(en.members.inviteEmail), "new+member@gmail.com");
    await userEvent.selectOptions(within(drawer).getByLabelText(en.members.role), "viewer");
    await userEvent.click(within(drawer).getByRole("button", { name: en.members.invite }));
    expect(await screen.findByText(en.members.invited)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ email: "new+member@gmail.com", role: "viewer" });
  });

  it("offers the owner role to an owner", async () => {
    mockApi(base());
    renderAt("/w/w1/members", route);
    await screen.findByText("Diğer Kişi");
    const drawer = await openInvite();
    expect(within(within(drawer).getByLabelText(en.members.role)).getAllByRole("option").map((o) => o.textContent)).toEqual(["Owner", "Admin", "Member", "Viewer"]);
  });

  it("does not offer the owner role to an admin who is not an owner", async () => {
    mockApi(base(meAs("admin")));
    renderAt("/w/w1/members", route);
    await screen.findByText("Diğer Kişi");
    const drawer = await openInvite();
    expect(within(within(drawer).getByLabelText(en.members.role)).getAllByRole("option").map((o) => o.textContent)).toEqual(["Admin", "Member", "Viewer"]);
  });

  it("closes without sending anything when the person closes it", async () => {
    const calls = mockApi(base());
    renderAt("/w/w1/members", route);
    await screen.findByText("Diğer Kişi");
    const drawer = await openInvite();
    await userEvent.click(within(drawer).getByRole("button", { name: en.common.close }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
  });

  it("shows a refused invitation inside the drawer, which stays open with the address typed", async () => {
    mockApi({ ...base(), "POST /workspaces/w1/invitations": { status: 409, body: { title: "conflict" } } });
    renderAt("/w/w1/members", route);
    await screen.findByText("Diğer Kişi");
    const drawer = await openInvite();
    await userEvent.type(within(drawer).getByLabelText(en.members.inviteEmail), "new+member@gmail.com");
    await userEvent.click(within(drawer).getByRole("button", { name: en.members.invite }));
    await waitFor(() => expect(within(screen.getByRole("dialog")).getByText(en.errors.conflict)).toBeInTheDocument());
    expect(within(screen.getByRole("dialog")).getByLabelText(en.members.inviteEmail)).toHaveValue("new+member@gmail.com");
  });
});
