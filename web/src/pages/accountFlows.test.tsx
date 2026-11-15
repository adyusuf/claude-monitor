import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AcceptInvitationPage, AccountPage } from "./AccountPages";

describe("turning two-step sign-in off", () => {
  it("sends the code, says it is off and offers the set-up again", async () => {
    let enabled = true;
    const calls = mockApi({
      "GET /me": () => ({ body: { ...ME, mfaEnabled: enabled } }),
      "POST /me/mfa/disable": () => {
        enabled = false;
        return { status: 204 };
      },
    });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    const [disableCode] = await screen.findAllByLabelText(/Code from your authenticator app/);
    await userEvent.type(disableCode!, "123456");
    await userEvent.click(screen.getByRole("button", { name: "Turn off" }));
    expect(await screen.findByText(en.account.mfaDisabled)).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Set up" })).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/me/mfa/disable")?.body).toEqual({ code: "123456" });
  });
});

describe("accepting an invitation", () => {
  const routes = [
    { path: "/invitations/accept", element: <AcceptInvitationPage /> },
    { path: "/w/:ws/projects", element: <div>workspace page</div> },
    { path: "/", element: <div>home page</div> },
  ];

  it("sends the token from the link and takes the person to the workspace they joined (F-058)", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "POST /invitations/accept": { body: { workspaceId: "w2" } },
    });
    renderAt("/invitations/accept?token=invite-1", routes);
    expect(await screen.findByText("workspace page")).toBeInTheDocument();
    expect(screen.queryByText("home page")).not.toBeInTheDocument();
    expect(calls.find((c) => c.path === "/invitations/accept")?.body).toEqual({ token: "invite-1" });
  });

  it("reads the signed-in user again after joining, so the new workspace shows", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "POST /invitations/accept": { body: { workspaceId: "w1" } },
    });
    renderAt("/invitations/accept?token=invite-1", routes);
    await screen.findByText("workspace page");
    // The page's own first read may come after the POST (the POST is sent from a child effect), so one read proves nothing: the refresh is the second.
    const reads = calls.filter((c) => c.method === "GET" && c.path === "/me");
    expect(reads.length).toBeGreaterThanOrEqual(2);
  });
});
