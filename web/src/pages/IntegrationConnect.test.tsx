import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { stubLocation } from "../test/integrations-fixtures";
import { IntegrationConnect } from "./IntegrationConnect";

afterEach(() => vi.unstubAllGlobals());
const open = () => renderAt("/c", [{ path: "/c", element: <IntegrationConnect ws="w1" /> }]);
const connectPath = "POST /workspaces/w1/integrations/jira/connect";
const twoStepMe = { "GET /me": { body: { ...ME, mfaEnabled: true } } };

describe("connecting Jira", () => {
  it("sends the trimmed sub-domain and goes to the address the API returns", async () => {
    const assign = stubLocation();
    const calls = mockApi({ ...twoStepMe, [connectPath]: { body: { authorizeUrl: "https://auth.atlassian.com/authorize?state=abc" } } });
    open();
    await userEvent.type(screen.getByLabelText("Jira address"), "  acme ");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith("https://auth.atlassian.com/authorize?state=abc"));
    const sent = calls.find((c) => c.method === "POST");
    expect(sent?.body).toEqual({ subdomain: "acme" });
    expect(screen.queryByLabelText("Code from your authenticator app")).not.toBeInTheDocument();
  });

  it("shows the translated problem for a bad address and does not navigate", async () => {
    const assign = stubLocation();
    mockApi({ ...twoStepMe, [connectPath]: { status: 400, body: { title: "invalid_site" } } });
    open();
    await userEvent.type(screen.getByLabelText("Jira address"), "not a site");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Enter your Jira sub-domain, such as acme for acme.atlassian.net.");
    expect(assign).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Code from your authenticator app")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Connect with Atlassian" })).toBeEnabled(); // it can be tried again
  });

  it("asks for the two-step code when the API wants a fresh sign-in, with the connect wording, and sends it on the retry", async () => {
    const assign = stubLocation();
    let attempt = 0;
    const calls = mockApi({
      ...twoStepMe,
      [connectPath]: () => ++attempt === 1
        ? { status: 403, body: { title: "reauth_required" } }
        : { body: { authorizeUrl: "https://auth.atlassian.com/authorize?state=two" } },
    });
    open();
    await userEvent.type(screen.getByLabelText("Jira address"), "acme");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    const code = await screen.findByLabelText("Code from your authenticator app");
    expect(assign).not.toHaveBeenCalled();
    expect(screen.getByText(en.remote.codeHintReauth)).toBeInTheDocument();
    expect(screen.queryByText(en.errors.reauth_required)).not.toBeInTheDocument(); // not the account-deletion text (F-226)
    expect(screen.getByRole("button", { name: "Connect with Atlassian" })).toBeDisabled(); // until the code is typed
    await userEvent.type(code, " 123456 ");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith("https://auth.atlassian.com/authorize?state=two"));
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts.map((p) => p.body)).toEqual([{ subdomain: "acme" }, { subdomain: "acme", code: "123456" }]);
  });

  it("keeps the code field with the typed code when the retry fails for another reason, and says so", async () => {
    stubLocation();
    let attempt = 0;
    mockApi({
      ...twoStepMe,
      [connectPath]: () => ++attempt === 1 ? { status: 403, body: { title: "reauth_required" } } : { status: 400, body: { title: "invalid_site" } },
    });
    open();
    await userEvent.type(screen.getByLabelText("Jira address"), "acme");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    await userEvent.type(await screen.findByLabelText("Code from your authenticator app"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    expect(await screen.findByText(/Enter your Jira sub-domain/)).toBeInTheDocument();
    expect(screen.getByLabelText("Code from your authenticator app")).toHaveValue("123456"); // the next try still needs it
  });

  it("tells a person without two-step sign-in to sign in again, offers no code field and keeps the button usable", async () => {
    const assign = stubLocation();
    const calls = mockApi({ "GET /me": { body: { ...ME, mfaEnabled: false } }, [connectPath]: { status: 403, body: { title: "reauth_required" } } });
    open();
    await userEvent.type(screen.getByLabelText("Jira address"), "acme");
    await userEvent.click(screen.getByRole("button", { name: "Connect with Atlassian" }));
    expect(await screen.findByText(en.integrations.reauthSignIn)).toBeInTheDocument();
    expect(screen.queryByLabelText("Code from your authenticator app")).not.toBeInTheDocument();
    expect(screen.queryByText(en.errors.reauth_required)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Connect with Atlassian" })).toBeEnabled();
    expect(assign).not.toHaveBeenCalled();
    expect(calls.filter((c) => c.method === "POST").map((p) => p.body)).toEqual([{ subdomain: "acme" }]);
  });
});
