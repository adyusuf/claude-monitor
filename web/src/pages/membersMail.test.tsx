import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, openTab, renderAt } from "../test/helpers";
import { MembersPage } from "./MembersPage";

const joined = "2026-01-01T00:00:00Z";
const members = [{ userId: "u1", displayName: "Owner Person", email: "owner+test@gmail.com", role: "owner", joinedAt: joined }];
const invitation = { id: "i1", email: "Invitee+Typed@Gmail.com", role: "member", createdAt: joined, expiresAt: "2026-02-01T00:00:00Z" };
const route = [{ path: "/w/:ws/members", element: <MembersPage /> }];
const base = { "GET /me": { body: ME }, "GET /workspaces/w1/members": { body: members }, "GET /workspaces/w1/invitations": { body: [invitation] } };

const inviteSomeone = async () => {
  await screen.findByText("Owner Person");
  await openTab(/Invitations/);
  await userEvent.click(screen.getByRole("button", { name: en.members.invite }));
  const drawer = screen.getByRole("dialog");
  await userEvent.type(within(drawer).getByLabelText(en.members.inviteEmail), "new+member@gmail.com");
  await userEvent.click(within(drawer).getByRole("button", { name: en.members.invite }));
};

describe("members: the invitation mail (F-047)", () => {
  it("says the mail was not sent when the invitation is saved without it", async () => {
    mockApi({ ...base, "POST /workspaces/w1/invitations": { status: 201, body: { id: "i2", mailSent: false } } });
    renderAt("/w/w1/members", route);
    await inviteSomeone();
    expect(await screen.findByText(en.members.mailNotSent)).toBeInTheDocument();
    expect(screen.queryByText(en.members.invited)).toBeNull();
  });

  it("says the invitation was sent when the answer leaves mailSent out (an older server)", async () => {
    mockApi({ ...base, "POST /workspaces/w1/invitations": { status: 201, body: { id: "i2" } } });
    renderAt("/w/w1/members", route);
    await inviteSomeone();
    expect(await screen.findByText(en.members.invited)).toBeInTheDocument();
  });

  it("shows the address as typed and sends the resend of an open invitation", async () => {
    const calls = mockApi({ ...base, "POST /workspaces/w1/invitations/i1/resend": { status: 200, body: { mailSent: true } } });
    renderAt("/w/w1/members", route);
    await screen.findByText("Owner Person");
    await openTab(/Invitations/);
    expect(screen.getByText("Invitee+Typed@Gmail.com")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: en.members.resend }));
    expect(await screen.findByText(en.members.resent)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST" && c.path === "/workspaces/w1/invitations/i1/resend")).toBe(true);
  });

  it("says the mail was not sent again when the resend cannot send it either", async () => {
    mockApi({ ...base, "POST /workspaces/w1/invitations/i1/resend": { status: 200, body: { mailSent: false } } });
    renderAt("/w/w1/members", route);
    await screen.findByText("Owner Person");
    await openTab(/Invitations/);
    await userEvent.click(screen.getByRole("button", { name: en.members.resend }));
    expect(await screen.findByText(en.members.mailNotSent)).toBeInTheDocument();
  });

  it("names an invitation that cannot be sent again because its exact address was not kept", async () => {
    mockApi({ ...base, "POST /workspaces/w1/invitations/i1/resend": { status: 409, body: { title: "invitation_address_unknown" } } });
    renderAt("/w/w1/members", route);
    await screen.findByText("Owner Person");
    await openTab(/Invitations/);
    await userEvent.click(screen.getByRole("button", { name: en.members.resend }));
    expect(await screen.findByText(en.errors.invitation_address_unknown)).toBeInTheDocument();
  });
});
