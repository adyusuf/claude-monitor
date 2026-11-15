import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useLocation } from "react-router";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AccountPage } from "./AccountPages";
import { ForgotPage, LoginPage } from "./AuthPages";
import { MfaPage } from "./Mfa";

function SignInStub() {
  return <div>sign-in page {useLocation().search}</div>;
}

const signedOut = { "GET /me": { status: 401 }, "GET /auth/providers": { body: { available: [] } } };

describe("forgotten password", () => {
  it("says so when the sending is rate limited instead of claiming a link is on its way (F-039)", async () => {
    mockApi({ ...signedOut, "POST /auth/password/forgot": { status: 429 } });
    renderAt("/forgot", [{ path: "/forgot", element: <ForgotPage /> }]);
    await userEvent.type(screen.getByLabelText("E-mail"), "who@gmail.com");
    await userEvent.click(screen.getByRole("button", { name: "Send the link" }));
    expect(await screen.findByText(en.errors.too_many_requests)).toBeInTheDocument();
    expect(screen.queryByText(/a reset link is on its way/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send the link" })).toBeInTheDocument();
  });

  it("keeps the neutral answer for any other failure (F-039)", async () => {
    mockApi({ ...signedOut, "POST /auth/password/forgot": { status: 500 } });
    renderAt("/forgot", [{ path: "/forgot", element: <ForgotPage /> }]);
    await userEvent.type(screen.getByLabelText("E-mail"), "who@gmail.com");
    await userEvent.click(screen.getByRole("button", { name: "Send the link" }));
    expect(await screen.findByText(/a reset link is on its way/)).toBeInTheDocument();
  });
});

describe("an error carried in the address", () => {
  it("is not shown on the sign-in page when it is not one of ours (F-040)", () => {
    mockApi(signedOut);
    renderAt("/login?error=pay_me_now", [{ path: "/login", element: <LoginPage /> }]);
    expect(screen.queryByText(/pay_me_now/)).not.toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("is not shown on the account page when it is not one of ours (F-040)", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/account?error=pay_me_now", [{ path: "/account", element: <AccountPage /> }]);
    await screen.findByText(en.account.title);
    expect(screen.queryByText(/pay_me_now/)).not.toBeInTheDocument();
  });

  it("is still shown when it is one of ours (F-040)", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/account?error=provider_in_use", [{ path: "/account", element: <AccountPage /> }]);
    expect(await screen.findByText(en.errors.provider_in_use)).toBeInTheDocument();
  });
});

describe("the second step of sign-in", () => {
  it("offers the way back to sign-in on the provider path, keeping where the person was going (F-186)", async () => {
    mockApi({ "GET /me": { status: 401 } });
    renderAt("/mfa?next=/w/w1/sessions", [{ path: "/mfa", element: <MfaPage /> }, { path: "/login", element: <SignInStub /> }]);
    await userEvent.click(await screen.findByRole("link", { name: en.auth.backToSignIn }));
    expect(await screen.findByText("sign-in page ?next=%2Fw%2Fw1%2Fsessions")).toBeInTheDocument();
  });

  it("returns from the code to the password form on the password path (F-186)", async () => {
    mockApi({ ...signedOut, "POST /auth/login": { status: 401, body: { title: "mfa_required", mfaToken: "pending-1" } } });
    renderAt("/login", [{ path: "/login", element: <LoginPage /> }]);
    await userEvent.type(screen.getByLabelText("E-mail"), "a+mfa@gmail.com");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await userEvent.click(await screen.findByRole("link", { name: en.auth.backToSignIn }));
    expect(await screen.findByLabelText("Password")).toBeInTheDocument();
    expect(screen.queryByLabelText(/Code from your authenticator app/)).not.toBeInTheDocument();
  });
});
