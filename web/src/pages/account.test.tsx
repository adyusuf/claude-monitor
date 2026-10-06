import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Layout } from "../components/Layout";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AccountPage } from "./AccountPages";

describe("the frame", () => {
  it("switches language and theme, and signs out", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "POST /auth/logout": { status: 204 } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }, { path: "/login", element: <div>login page</div> }]);
    expect(await screen.findByRole("link", { name: /Sessions/ })).toHaveAttribute("href", "/w/w1/sessions");
    expect(screen.getByRole("link", { name: /Workspace settings/ })).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Language"), "tr");
    expect(await screen.findByRole("link", { name: /Oturumlar/ })).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Tema"), "dark");
    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");
    await userEvent.selectOptions(screen.getByLabelText("Tema"), "system");
    expect(document.documentElement.hasAttribute("data-theme")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Menu" }));
    await userEvent.click(screen.getByRole("button", { name: "Çıkış" }));
    expect(await screen.findByText("login page")).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/auth/logout")).toBe(true);
  });
});

describe("the user's own data", () => {
  it("offers the export and deletes the account with the password and the word", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "POST /me/delete": { status: 204 } });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    expect(await screen.findByRole("link", { name: "Download my data" })).toHaveAttribute("href", "/api/me/export");
    await userEvent.type(screen.getByLabelText("Password"), "my password!!");
    await userEvent.type(screen.getByLabelText("Type DELETE to confirm"), "delete");
    await userEvent.click(screen.getByRole("button", { name: "Delete my account" }));
    expect(screen.getByText("Type the confirmation word exactly.")).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/me/delete")).toBe(false);
    await userEvent.clear(screen.getByLabelText("Type DELETE to confirm"));
    await userEvent.type(screen.getByLabelText("Type DELETE to confirm"), "DELETE");
    await userEvent.click(screen.getByRole("button", { name: "Delete my account" }));
    expect(await screen.findByText("Your account is deleted.")).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/me/delete")?.body).toEqual({ password: "my password!!", confirm: "DELETE" });
  });

  it("explains why a sole owner cannot leave yet, and asks no password of a provider-only account", async () => {
    const calls = mockApi({ "GET /me": { body: { ...ME, hasPassword: false } }, "POST /me/delete": { status: 409, body: { title: "sole_owner" } } });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    await userEvent.type(await screen.findByLabelText("Type DELETE to confirm"), "DELETE");
    expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Delete my account" }));
    expect(await screen.findByText(/only owner of a shared workspace/)).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/me/delete")?.body).toEqual({ confirm: "DELETE" });
  });

  it("tells a provider-only account to sign in again when the API asks for a fresh sign-in", async () => {
    const calls = mockApi({
      "GET /me": { body: { ...ME, hasPassword: false, mfaEnabled: false } },
      "POST /me/delete": { status: 403, body: { title: "reauth_required" } },
    });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    await userEvent.type(await screen.findByLabelText("Type DELETE to confirm"), "DELETE");
    await userEvent.click(screen.getByRole("button", { name: "Delete my account" }));
    expect(await screen.findByText(en.errors.reauth_required)).toBeInTheDocument();
    expect(screen.queryByText("Your account is deleted.")).not.toBeInTheDocument();
    const body = calls.find((c) => c.path === "/me/delete")?.body as object;
    expect(body).toEqual({ confirm: "DELETE" });
    expect(body).not.toHaveProperty("password");
    expect(body).not.toHaveProperty("code");
  });
});
