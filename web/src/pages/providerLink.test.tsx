import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AccountPage } from "./AccountPages";

const route = [{ path: "/settings", element: <AccountPage /> }];

describe("linked sign-in methods (F-181)", () => {
  it("unlinks a linked provider and says so", async () => {
    let providers = ["github"];
    const calls = mockApi({
      "GET /me": () => ({ body: { ...ME, providers } }),
      "DELETE /me/providers/github": () => {
        providers = [];
        return { status: 204 };
      },
    });
    renderAt("/settings", route);
    await userEvent.click(await screen.findByRole("button", { name: "Unlink GitHub" }));
    expect(await screen.findByText("GitHub is unlinked.")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "DELETE" && c.path === "/me/providers/github")).toBe(true);
    expect(await screen.findByRole("link", { name: "Link GitHub" })).toBeInTheDocument();
  });

  it("words a refused stale sign-in for unlinking, and names the last way in", async () => {
    mockApi({
      "GET /me": { body: { ...ME, hasPassword: false, providers: ["github", "google"] } },
      "DELETE /me/providers/github": { status: 403, body: { title: "reauth_required" } },
      "DELETE /me/providers/google": { status: 409, body: { title: "last_sign_in_method" } },
    });
    renderAt("/settings", route);
    await userEvent.click(await screen.findByRole("button", { name: "Unlink GitHub" }));
    expect(await screen.findByText(en.account.providerFreshSignIn)).toBeInTheDocument();
    expect(screen.queryByText(en.errors.reauth_required)).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Unlink Google" }));
    expect(await screen.findByText(en.errors.last_sign_in_method)).toBeInTheDocument();
  });

  it("explains a link refused for a stale sign-in", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/settings?error=link_reauth_required", route);
    expect(await screen.findByText(en.errors.link_reauth_required)).toBeInTheDocument();
  });
});
