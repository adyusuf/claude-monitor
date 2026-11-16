import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { MembersPage } from "./MembersPage";

const joined = "2026-01-01T00:00:00Z";
const members = [
  { userId: "u1", displayName: "Owner Person", email: "owner+test@gmail.com", role: "owner", joinedAt: joined },
  { userId: "u2", displayName: "Assigned Person", email: "assigned+test@gmail.com", role: "member", joinedAt: joined },
];
const route = [{ path: "/w/:ws/members", element: <MembersPage /> }];
const base = { "GET /me": { body: ME }, "GET /workspaces/w1/members": { body: members }, "GET /workspaces/w1/invitations": { body: [] } };
const refused = { status: 409, body: { title: "member_has_issues", count: 2, archivedCount: 1 } };

describe("members with issues assigned (F-102)", () => {
  it("shows why a removal is refused", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockApi({ ...base, "DELETE /workspaces/w1/members/u2": refused });
    renderAt("/w/w1/members", route);
    await userEvent.click(await screen.findByRole("button", { name: en.members.remove }));
    expect(await screen.findByText(en.errors.member_has_issues)).toBeInTheDocument();
  });

  it("shows why a lowering is refused", async () => {
    mockApi({ ...base, "PATCH /workspaces/w1/members/u2": refused });
    renderAt("/w/w1/members", route);
    await userEvent.selectOptions(await screen.findByLabelText(en.members.role), "viewer");
    expect(await screen.findByText(en.errors.member_has_issues)).toBeInTheDocument();
  });
});
