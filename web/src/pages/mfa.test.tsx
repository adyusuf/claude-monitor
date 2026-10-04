import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AccountPage } from "./AccountPages";
import { LoginPage } from "./AuthPages";
import { MfaPage } from "./Mfa";

describe("two-step sign-in", () => {
  it("asks for the code after the password and signs in with it", async () => {
    let signedIn = false;
    const calls = mockApi({
      "GET /me": () => (signedIn ? { body: ME } : { status: 401 }),
      "GET /auth/providers": { body: { available: [] } },
      "POST /auth/login": { status: 401, body: { title: "mfa_required", mfaToken: "pending-1" } },
      "POST /auth/mfa": (c) => {
        if ((c.body as { code: string }).code !== "123456") return { status: 400, body: { errors: { code: ["invalid_code"] } } };
        signedIn = true;
        return { status: 204 };
      },
    });
    renderAt("/login?next=/w/w1/sessions", [{ path: "/login", element: <LoginPage /> }, { path: "/w/:ws/sessions", element: <div>sessions page</div> }]);
    await userEvent.type(screen.getByLabelText("E-mail"), "a+mfa@gmail.com");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("heading", { name: "Two-step sign-in" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/Code from your authenticator app/), "000000");
    await userEvent.click(screen.getByRole("button", { name: "Verify" }));
    expect(await screen.findByText("That code is not valid or has expired.")).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText(/Code from your authenticator app/));
    await userEvent.type(screen.getByLabelText(/Code from your authenticator app/), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Verify" }));
    expect(await screen.findByText("sessions page")).toBeInTheDocument();
    expect(calls.filter((c) => c.path === "/auth/mfa").at(-1)?.body).toEqual({ token: "pending-1", code: "123456" });
  });

  it("posts only the code after a provider sign-in and ignores a token in the address", async () => {
    const calls = mockApi({ "GET /me": { status: 401 }, "POST /auth/mfa": { status: 204 } });
    renderAt("/mfa?token=leaked", [{ path: "/mfa", element: <MfaPage /> }, { path: "/", element: <div>home</div> }]);
    await userEvent.type(await screen.findByLabelText(/Code from your authenticator app/), "654321");
    await userEvent.click(screen.getByRole("button", { name: "Verify" }));
    expect(await screen.findByText("home")).toBeInTheDocument();
    const body = calls.find((c) => c.path === "/auth/mfa")?.body as object;
    expect(Object.keys(body)).toEqual(["code"]);
    expect(body).toEqual({ code: "654321" });
  });
});

describe("turning two-step sign-in on and off", () => {
  it("shows the key, enables with a code and shows the recovery codes once", async () => {
    let enabled = false;
    mockApi({
      "GET /me": () => ({ body: { ...ME, mfaEnabled: enabled } }),
      "POST /me/mfa/setup": { body: { secret: "JBSWY3DPEHPK3PXP", uri: "otpauth://totp/x?secret=JBSWY3DPEHPK3PXP" } },
      "POST /me/mfa/enable": () => {
        enabled = true;
        return { body: { recoveryCodes: ["aaaaa-bbbbb", "ccccc-ddddd"] } };
      },
    });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Set up" }));
    expect(await screen.findByText("JBSWY3DPEHPK3PXP")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open in authenticator" })).toHaveAttribute("href", "otpauth://totp/x?secret=JBSWY3DPEHPK3PXP");
    await userEvent.type(screen.getAllByLabelText(/Code from your authenticator app/)[0]!, "123456");
    await userEvent.click(screen.getByRole("button", { name: "Turn on" }));
    expect(await screen.findByText("aaaaa-bbbbb")).toBeInTheDocument();
    expect(screen.getByText(/They are shown only now/)).toBeInTheDocument();
  });

  it("turns it off with a code and asks for a code before deleting the account", async () => {
    const calls = mockApi({
      "GET /me": { body: { ...ME, mfaEnabled: true } },
      "POST /me/mfa/disable": { status: 400, body: { errors: { code: ["invalid_code"] } } },
      "POST /me/delete": { status: 204 },
    });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    const [disableCode, deleteCode] = await screen.findAllByLabelText(/Code from your authenticator app/);
    await userEvent.type(disableCode!, "111111");
    await userEvent.click(screen.getByRole("button", { name: "Turn off" }));
    expect(await screen.findByText("That code is not valid or has expired.")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Password"), "my password!!");
    await userEvent.type(deleteCode!, "222222");
    await userEvent.type(screen.getByLabelText("Type DELETE to confirm"), "DELETE");
    await userEvent.click(screen.getByRole("button", { name: "Delete my account" }));
    expect(await screen.findByText("Your account is deleted.")).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/me/delete")?.body).toEqual({ password: "my password!!", confirm: "DELETE", code: "222222" });
  });
});
