import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ConnectionView } from "../api/typesIntegrations";
import { en } from "../i18n/en";
import { ME, renderAt, type Call } from "../test/helpers";
import { connection, localIso, serveIntegrations as serve } from "../test/integrations-fixtures";
import { IntegrationRow } from "./IntegrationRow";

afterEach(() => vi.restoreAllMocks());

const open = (over: Partial<ConnectionView> = {}, admin = true) => {
  const onOpen = vi.fn();
  const onChange = vi.fn();
  const onError = vi.fn();
  renderAt("/", [{ path: "/", element: <table><tbody><IntegrationRow connection={connection(over)} admin={admin} onOpen={onOpen} onChange={onChange} onError={onError} /></tbody></table> }]);
  return { onOpen, onChange, onError };
};
const writes = (calls: Call[]) => calls.filter((c) => c.method !== "GET" && c.path.startsWith("/integrations/c1"));

describe("a connection row: how it stands", () => {
  it("shows the site, the green Connected badge, who connected it and how many projects came in", async () => {
    serve();
    open();
    const row = within(await screen.findByRole("row"));
    expect(row.getByText("acme.atlassian.net")).toBeInTheDocument();
    expect(row.getByText("Connected")).toHaveClass("badge", "ok");
    expect(row.getByText("Örnek Kişi")).toBeInTheDocument();
    expect(row.getByText("2")).toBeInTheDocument();
    expect(row.queryByText(/must sign in again/)).not.toBeInTheDocument();
  });

  it("shows the Sign in again badge in warning colour and the needs-sign-in line for a connection that needs it", async () => {
    serve();
    open({ status: "reauthorize" });
    const row = within(await screen.findByRole("row"));
    expect(row.getByText("Sign in again")).toHaveClass("badge", "warn");
    expect(row.getByText(/The account that connected this site must sign in again\./)).toBeInTheDocument();
  });

  it("shows the date of the last check as dd/mm/yyyy hh:mm, and says so when there was none", async () => {
    serve();
    open({ lastPassAt: localIso(2026, 10, 3, 7, 5) });
    expect(await screen.findByText("03/10/2026 07:05")).toBeInTheDocument();
  });

  it("says it was not checked yet when there was no check", async () => {
    serve();
    open({ lastPassAt: null });
    expect(await screen.findByText("Not checked yet")).toBeInTheDocument();
  });

  it("shows the last problem in words, from its code, and nothing when there is none", async () => {
    serve();
    open({ lastError: "tracker_unavailable" });
    expect(await screen.findByText(/Last problem: Jira cannot be reached right now\./)).toBeInTheDocument();
  });

  it("shows no last problem line without an error", async () => {
    serve();
    open({ lastError: null });
    await screen.findByRole("row");
    expect(screen.queryByText(/Last problem/)).not.toBeInTheDocument();
  });
});

describe("a connection row: the links that open the drawers", () => {
  it.each([["Spaces", "spaces"], ["People from Jira", "people"], ["Recent checks", "checks"], ["Settings", "settings"]])("opens the %s drawer", async (name, panel) => {
    serve();
    const { onOpen } = open();
    await userEvent.click(await screen.findByRole("button", { name }));
    expect(onOpen).toHaveBeenCalledWith(panel);
  });

  it("gives a person who is not an admin every link but the people", async () => {
    serve();
    open({}, false);
    await screen.findByRole("row");
    for (const name of ["Spaces", "Recent checks", "Settings"]) expect(screen.getByRole("button", { name })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "People from Jira" })).not.toBeInTheDocument();
  });
});

describe("a connection row: the admin actions", () => {
  it("checks now: starts the check, tells the page and ends the busy state", async () => {
    const calls = serve({ "POST /integrations/c1/sync": { body: { id: "r2" } } });
    const { onChange } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Check now" }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(writes(calls)).toEqual([{ method: "POST", path: "/integrations/c1/sync", body: undefined }]);
    expect(await screen.findByRole("button", { name: "Check now" })).toBeEnabled();
  });

  it("hands the translated problem to the page when the check fails, and does not tell it the connection changed", async () => {
    serve({ "POST /integrations/c1/sync": { status: 409, body: { title: "reauthorize" } } });
    const { onChange, onError } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Check now" }));
    await waitFor(() => expect(onError).toHaveBeenCalledWith("The connection needs a new sign-in at Atlassian."));
    expect(onChange).not.toHaveBeenCalled();
  });

  it("disconnects after the person says yes, naming the site in the question", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = serve({ "POST /integrations/c1/disconnect": { status: 204 } });
    const { onChange } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(confirm).toHaveBeenCalledWith("Disconnect acme.atlassian.net? What was imported stays, but stops updating.");
    // F-081: the address that carries the code; on a recent sign-in it goes without one.
    expect(writes(calls)).toEqual([{ method: "POST", path: "/integrations/c1/disconnect", body: {} }]);
  });

  it("asks a person with two-step sign-in for the code when the API wants a recent sign-in, and sends it on the second click", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    let attempt = 0;
    const calls = serve({
      "GET /me": { body: { ...ME, mfaEnabled: true } },
      "POST /integrations/c1/disconnect": () => ++attempt === 1 ? { status: 403, body: { title: "reauth_required" } } : { status: 204 },
    });
    const { onChange } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    const field = await screen.findByLabelText("Code from your authenticator app");
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Disconnect" })).toBeDisabled(); // waits for the code
    await userEvent.type(field, " 123456 ");
    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(confirm).toHaveBeenCalledTimes(1); // the question is not asked again
    expect(writes(calls)).toEqual([
      { method: "POST", path: "/integrations/c1/disconnect", body: {} },
      { method: "POST", path: "/integrations/c1/disconnect", body: { code: "123456" } },
    ]);
  });

  it("tells a person without two-step sign-in to sign in again, with no code field", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve({ "GET /me": { body: { ...ME, mfaEnabled: false } }, "POST /integrations/c1/disconnect": { status: 403, body: { title: "reauth_required" } } });
    const { onChange } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(await screen.findByText(en.integrations.disconnectSignIn)).toBeInTheDocument();
    expect(screen.queryByLabelText("Code from your authenticator app")).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("shows the translated problem when disconnecting fails for another reason", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    serve({ "POST /integrations/c1/disconnect": { status: 409, body: { title: "reauthorize" } } });
    const { onChange } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(await screen.findByText("The connection needs a new sign-in at Atlassian.")).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("does nothing when the person says no", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = serve({ "POST /integrations/c1/disconnect": { status: 204 } });
    const { onChange } = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(writes(calls)).toEqual([]);
    expect(onChange).not.toHaveBeenCalled();
  });

  it("gives a person who is not an admin neither check now nor disconnect", async () => {
    serve();
    open({}, false);
    await screen.findByRole("row");
    expect(screen.queryByRole("button", { name: "Check now" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Disconnect" })).not.toBeInTheDocument();
  });
});
