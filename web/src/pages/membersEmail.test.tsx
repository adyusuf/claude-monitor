import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { meAs } from "../test/projects-fixtures";
import { MembersPage } from "./MembersPage";

const joined = "2026-01-01T00:00:00Z";
const route = [{ path: "/w/:ws/members", element: <MembersPage /> }];

describe("members: the e-mail column below admin (F-225)", () => {
  it("shows a name and no empty address line when the server leaves the address out", async () => {
    const members = [
      { userId: "u1", displayName: "Hidden Address", email: null, role: "owner", joinedAt: joined },
      { userId: "u2", displayName: "Other Person", email: null, role: "member", joinedAt: joined },
    ];
    mockApi({ "GET /me": { body: meAs("viewer") }, "GET /workspaces/w1/members": { body: members } });
    renderAt("/w/w1/members", route);
    const cell = (await screen.findByText("Hidden Address")).closest("td");
    expect(cell?.querySelector(".muted")).toBeNull();
    expect(screen.getByText("Other Person")).toBeInTheDocument();
  });

  it("still shows the address of each member to an admin", async () => {
    const members = [{ userId: "u1", displayName: "Visible Address", email: "visible+test@gmail.com", role: "owner", joinedAt: joined }];
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/members": { body: members }, "GET /workspaces/w1/invitations": { body: [] } });
    renderAt("/w/w1/members", route);
    expect(await screen.findByText("visible+test@gmail.com")).toBeInTheDocument();
  });
});
