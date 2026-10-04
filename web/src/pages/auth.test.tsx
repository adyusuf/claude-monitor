import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App } from "../App";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { ForgotPage, LoginPage, RegisterPage, ResetPasswordPage, VerifyEmailPage } from "./AuthPages";

const signedOut = { "GET /me": { status: 401 }, "GET /auth/providers": { body: { available: ["github", "google"] } } };

describe("sign in", () => {
  it("signs in and goes where it was asked to", async () => {
    let signedIn = false;
    const calls = mockApi({
      ...signedOut,
      "GET /me": () => (signedIn ? { body: ME } : { status: 401 }),
      "POST /auth/login": () => {
        signedIn = true;
        return { status: 204 };
      },
    });
    renderAt("/login?next=/w/w1/sessions", [{ path: "/login", element: <LoginPage /> }, { path: "/w/:ws/sessions", element: <div>sessions page</div> }]);
    await userEvent.type(screen.getByLabelText("E-mail"), "ayse+t@gmail.com");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("sessions page")).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/auth/login")?.body).toEqual({ email: "ayse+t@gmail.com", password: "correct horse battery" });
    expect(screen.queryByText("Continue with GitHub")).not.toBeInTheDocument();
  });

  it("says why sign-in failed and offers the providers", async () => {
    mockApi({ ...signedOut, "POST /auth/login": { status: 401, body: { title: "invalid_credentials" } } });
    renderAt("/login?error=link_required", [{ path: "/login", element: <LoginPage /> }]);
    expect(screen.getByText(/An account with this address exists/)).toBeInTheDocument();
    expect(await screen.findByText("Continue with GitHub")).toHaveAttribute("href", "/api/auth/external/github?mode=signin");
    await userEvent.type(screen.getByLabelText("E-mail"), "x@gmail.com");
    await userEvent.type(screen.getByLabelText("Password"), "whatever-long");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText(en.errors.invalid_credentials)).toBeInTheDocument();
    expect(screen.getByText(/locked for a while/)).toBeInTheDocument();
    expect(screen.getByText(/resetting the password opens it at once/)).toBeInTheDocument();
  });
});

describe("register", () => {
  it("checks the fields before sending and then asks to check the mailbox", async () => {
    const calls = mockApi({ ...signedOut, "POST /auth/register": { status: 202 } });
    renderAt("/register", [{ path: "/register", element: <RegisterPage /> }]);
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(screen.getByText("That does not look like an e-mail address.")).toBeInTheDocument();
    expect(screen.getByText("Use at least 10 characters.")).toBeInTheDocument();
    expect(screen.getByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/auth/register")).toBe(false);
    await userEvent.type(screen.getByLabelText("Your name"), "Örnek Kişi");
    await userEvent.type(screen.getByLabelText("E-mail"), "ayse+r@gmail.com");
    await userEvent.type(screen.getByLabelText("Password"), "long enough pw");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByText(/Check your mailbox/)).toBeInTheDocument();
  });

  it("shows the API's field errors", async () => {
    mockApi({ ...signedOut, "POST /auth/register": { status: 400, body: { errors: { email: ["invalid_email"] } } } });
    renderAt("/register", [{ path: "/register", element: <RegisterPage /> }]);
    await userEvent.type(screen.getByLabelText("Your name"), "A");
    await userEvent.type(screen.getByLabelText("E-mail"), "a@b.co");
    await userEvent.type(screen.getByLabelText("Password"), "long enough pw");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByText("That does not look like an e-mail address.")).toBeInTheDocument();
  });
});

describe("mail links", () => {
  it("confirms an address once from the link", async () => {
    const calls = mockApi({ ...signedOut, "POST /auth/verify-email": { status: 204 } });
    renderAt("/verify-email?token=abc", [{ path: "/verify-email", element: <VerifyEmailPage /> }]);
    expect(await screen.findByText(/Your address is confirmed/)).toBeInTheDocument();
    expect(calls.filter((c) => c.path === "/auth/verify-email")).toHaveLength(1);
  });

  it("says when a link is spent", async () => {
    mockApi({ ...signedOut, "POST /auth/verify-email": { status: 400, body: { errors: { token: ["invalid_token"] } } } });
    renderAt("/verify-email?token=old", [{ path: "/verify-email", element: <VerifyEmailPage /> }]);
    expect(await screen.findByText("This link is invalid or has expired.")).toBeInTheDocument();
  });

  it("sends a reset link without telling whether the address exists", async () => {
    mockApi({ ...signedOut, "POST /auth/password/forgot": { status: 202 } });
    renderAt("/forgot", [{ path: "/forgot", element: <ForgotPage /> }]);
    await userEvent.type(screen.getByLabelText("E-mail"), "who@gmail.com");
    await userEvent.click(screen.getByRole("button", { name: "Send the link" }));
    expect(await screen.findByText(/a reset link is on its way/)).toBeInTheDocument();
  });

  it("sets a new password from the reset link", async () => {
    const calls = mockApi({ ...signedOut, "POST /auth/password/reset": { status: 204 } });
    renderAt("/reset-password?token=tok", [{ path: "/reset-password", element: <ResetPasswordPage /> }]);
    await userEvent.type(screen.getByLabelText("New password"), "short");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(screen.getByText("Use at least 10 characters.")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("New password"), " but now long");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText(/Your password is changed/)).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/auth/password/reset")?.body).toEqual({ token: "tok", password: "short but now long" });
  });

  it("shows a failed reset", async () => {
    mockApi({ ...signedOut, "POST /auth/password/reset": { status: 400, body: { errors: { token: ["invalid_token"] } } } });
    renderAt("/reset-password?token=tok", [{ path: "/reset-password", element: <ResetPasswordPage /> }]);
    await userEvent.type(screen.getByLabelText("New password"), "long enough now");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("This link is invalid or has expired.")).toBeInTheDocument();
  });
});

describe("routing", () => {
  it("sends a signed-out visitor to sign in, with the way back", async () => {
    mockApi(signedOut);
    renderAt("/w/w1/sessions", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
  });

  it("opens the first workspace, and a user without one creates one", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { body: { items: [], next: null } } });
    renderAt("/", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { name: "Sessions" })).toBeInTheDocument();
  });

  it("shows not found inside the frame", async () => {
    mockApi({ "GET /me": { body: { ...ME, workspaces: [] } } });
    renderAt("/nowhere", [{ path: "*", element: <App /> }]);
    expect(await screen.findByText("Page not found")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("link", { name: "Go to sessions" })).toBeInTheDocument());
  });
});
