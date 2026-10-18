import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Layout } from "../components/Layout";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { AccountPage } from "./AccountPages";
import { TwoStepPage } from "./TwoStepPage";

const SETUP = { secret: "JBSWY3DPEHPK3PXP", uri: "otpauth://totp/Claude%20Monitor:ayse?secret=JBSWY3DPEHPK3PXP&issuer=Claude%20Monitor" };
const route = [{ path: "/two-step", element: <TwoStepPage /> }];
const off = { ...ME, mfaEnabled: false };
const on = { ...ME, mfaEnabled: true };

describe("the authenticator guide page", () => {
  it("explains the code and lists the five setup steps, and says two-step sign-in is off", async () => {
    mockApi({ "GET /me": { body: off } });
    renderAt("/two-step", route);
    expect(await screen.findByRole("heading", { name: en.twoStep.title })).toBeInTheDocument();
    expect(screen.getByText(en.twoStep.intro)).toBeInTheDocument();
    for (const step of [en.twoStep.step1, en.twoStep.step2, en.twoStep.step3, en.twoStep.step4, en.twoStep.step5]) {
      expect(screen.getByText(step)).toBeInTheDocument();
    }
    expect(screen.getByText(en.twoStep.after)).toBeInTheDocument();
    expect(screen.getByText(en.twoStep.off)).toBeInTheDocument();
    expect(screen.queryByText(en.twoStep.on)).toBeNull();
    expect(screen.queryByRole("link", { name: en.twoStep.guide })).toBeNull(); // this IS the guide
  });

  it("shows the QR code and the key to type, then turns it on with the first code and shows the recovery codes", async () => {
    const calls = mockApi({
      "GET /me": { body: off },
      "POST /me/mfa/setup": { body: SETUP },
      "POST /me/mfa/enable": { body: { recoveryCodes: ["aaaa-1111", "bbbb-2222"] } },
    });
    renderAt("/two-step", route);
    await userEvent.click(await screen.findByRole("button", { name: en.account.mfaSetup }));
    expect(await screen.findByRole("img", { name: en.twoStep.qrLabel })).toBeInTheDocument();
    expect(screen.getByText(en.twoStep.manual)).toBeInTheDocument();
    expect(screen.getByText(SETUP.secret)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: en.account.mfaOpen })).toHaveAttribute("href", SETUP.uri);

    await userEvent.type(screen.getByLabelText(en.auth.mfaCode), "123456");
    await userEvent.click(screen.getByRole("button", { name: en.account.mfaEnable }));
    expect(await screen.findByText("aaaa-1111")).toBeInTheDocument();
    expect(screen.getByText("bbbb-2222")).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/me/mfa/enable")!.body).toEqual({ code: "123456" });
  });

  it("says it is on, and offers no setup, once two-step sign-in is on", async () => {
    mockApi({ "GET /me": { body: on } });
    renderAt("/two-step", route);
    expect(await screen.findByText(en.twoStep.on)).toBeInTheDocument();
    expect(screen.queryByText(en.twoStep.off)).toBeNull();
    expect(screen.queryByRole("button", { name: en.account.mfaSetup })).toBeNull();
  });
});

describe("where the guide is linked", () => {
  it("is in the sidebar for everyone, with a 'not set up' badge only while two-step sign-in is off", async () => {
    mockApi({ "GET /me": { body: off } });
    const first = renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    const link = await screen.findByRole("link", { name: new RegExp(en.twoStep.nav) });
    expect(link).toHaveAttribute("href", "/two-step");
    expect(link).toHaveTextContent(en.twoStep.navOff);
    first.unmount();

    mockApi({ "GET /me": { body: on } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    const again = await screen.findByRole("link", { name: new RegExp(en.twoStep.nav) });
    expect(again).not.toHaveTextContent(en.twoStep.navOff);
  });

  it("is next to the account's two-step card while it is off, and not once it is on", async () => {
    mockApi({ "GET /me": { body: off } });
    const first = renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    expect(await screen.findByRole("link", { name: en.twoStep.guide })).toHaveAttribute("href", "/two-step");
    first.unmount();

    mockApi({ "GET /me": { body: on } });
    renderAt("/account", [{ path: "/account", element: <AccountPage /> }]);
    await screen.findByText(en.account.mfaOn);
    expect(screen.queryByRole("link", { name: en.twoStep.guide })).toBeNull();
  });
});
