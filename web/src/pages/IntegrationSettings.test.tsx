import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ConnectionView } from "../api/typesIntegrations";
import { renderAt, type Call } from "../test/helpers";
import { connection, localIso, serveIntegrations as serve, stubLocation } from "../test/integrations-fixtures";
import { IntegrationSettings } from "./IntegrationSettings";

afterEach(() => vi.unstubAllGlobals());

const open = (over: Partial<ConnectionView> = {}, admin = true) => {
  const onChange = vi.fn();
  renderAt("/", [{ path: "/", element: <IntegrationSettings connection={connection(over)} admin={admin} onChange={onChange} /> }]);
  return onChange;
};
const writes = (calls: Call[]) => calls.filter((c) => c.method !== "GET" && c.path.startsWith("/integrations/c1"));

describe("the settings of a connection: how it stands", () => {
  it("says who connected it, how many projects came in and when it was last checked", async () => {
    serve();
    open({ lastPassAt: localIso(2026, 10, 3, 7, 5) });
    expect(await screen.findByText(/Connected by Örnek Kişi/)).toBeInTheDocument();
    expect(screen.getByText(/Projects imported: 2/)).toBeInTheDocument();
    expect(screen.getByText(/Last check 03\/10\/2026 07:05/)).toBeInTheDocument();
  });

  it("says it was not checked yet when there was no check", async () => {
    serve();
    open({ lastPassAt: null });
    expect(await screen.findByText(/Not checked yet/)).toBeInTheDocument();
    expect(screen.queryByText(/Last check/)).not.toBeInTheDocument();
  });

  it("shows the needs-sign-in notice for a connection that needs it, and not otherwise", async () => {
    serve();
    open({ status: "reauthorize" });
    expect(await screen.findByRole("alert")).toHaveTextContent("The account that connected this site must sign in again.");
  });

  it("shows no needs-sign-in notice for a connected one", async () => {
    serve();
    open();
    await screen.findByText(/Connected by/);
    expect(screen.queryByText(/must sign in again/)).not.toBeInTheDocument();
  });

  it("says live updates are on until the webhook's end date", async () => {
    serve();
    open({ webhookActive: true, webhookExpiresAt: localIso(2026, 11, 20, 8, 30) });
    expect(await screen.findByText("Live updates on (until 20/11/2026 08:30)")).toBeInTheDocument();
  });

  it.each([
    ["not active", { webhookActive: false, webhookExpiresAt: localIso(2026, 11, 20) }],
    ["active but without an end date", { webhookActive: true, webhookExpiresAt: null }],
  ])("says live updates are off when the webhook is %s", async (_name, over) => {
    serve();
    open(over);
    expect(await screen.findByText(/Live updates off: changes arrive within 10 minutes/)).toBeInTheDocument();
    expect(screen.queryByText(/Live updates on/)).not.toBeInTheDocument();
  });

  it("shows the last problem in words, from its code, and nothing when there is none", async () => {
    serve();
    open({ lastError: "tracker_unavailable" });
    expect(await screen.findByText(/Last problem: Jira cannot be reached right now\./)).toBeInTheDocument();
  });

  it("shows no last problem line without an error", async () => {
    serve();
    open({ lastError: null });
    await screen.findByText(/Connected by/);
    expect(screen.queryByText(/Last problem/)).not.toBeInTheDocument();
  });
});

describe("the settings of a connection: your own account", () => {
  it("says the account is linked and offers to sign in again", async () => {
    serve();
    open({ myAccountLinked: true, myAccountStatus: "active" });
    expect(await screen.findByText("Linked")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sign in again" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Link my account" })).not.toBeInTheDocument();
  });

  it("says the account is not linked and what that means, and offers to link it", async () => {
    serve();
    open({ myAccountLinked: false, myAccountStatus: null });
    expect(await screen.findByText(/Not linked: you can read, but changes you make are not sent to Jira\./)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Link my account" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sign in again" })).not.toBeInTheDocument();
  });

  it("says the link needs a new sign-in", async () => {
    serve();
    open({ myAccountLinked: true, myAccountStatus: "reauthorize" });
    expect(await screen.findByText("Linked, sign in again")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sign in again" })).toBeInTheDocument();
  });

  it("asks the API for the address and goes there, for a person who is not an admin too", async () => {
    const assign = stubLocation();
    const calls = serve({ "POST /integrations/c1/account/connect": { body: { authorizeUrl: "https://auth.atlassian.com/authorize?state=me" } } });
    const onChange = open({ myAccountLinked: false, myAccountStatus: null }, false);
    await userEvent.click(await screen.findByRole("button", { name: "Link my account" }));
    await waitFor(() => expect(assign).toHaveBeenCalledWith("https://auth.atlassian.com/authorize?state=me"));
    expect(calls.some((c) => c.method === "POST" && c.path === "/integrations/c1/account/connect")).toBe(true);
    await waitFor(() => expect(onChange).toHaveBeenCalled());
  });

  it("shows the translated problem and does not navigate when linking fails", async () => {
    const assign = stubLocation();
    serve({ "POST /integrations/c1/account/connect": { status: 400, body: { title: "integration_unavailable" } } });
    const onChange = open({ myAccountLinked: true, myAccountStatus: "reauthorize" });
    await userEvent.click(await screen.findByRole("button", { name: "Sign in again" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Jira is not set up on this server.");
    expect(assign).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
  });
});

describe("the settings of a connection: the invitation switch", () => {
  it("turns invitations on with the checkbox and tells the page", async () => {
    const calls = serve({ "PUT /integrations/c1": { status: 204 } });
    const onChange = open({ autoInvite: false });
    const box = await screen.findByRole("checkbox", { name: /Invite Jira people to Podium/ });
    expect(box).not.toBeChecked();
    await userEvent.click(box);
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(writes(calls)).toEqual([{ method: "PUT", path: "/integrations/c1", body: { autoInvite: true } }]);
  });

  it("turns invitations off when the box was on", async () => {
    const calls = serve({ "PUT /integrations/c1": { status: 204 } });
    open({ autoInvite: true });
    const box = await screen.findByRole("checkbox", { name: /Invite Jira people to Podium/ });
    expect(box).toBeChecked();
    await userEvent.click(box);
    await waitFor(() => expect(writes(calls)).toHaveLength(1));
    expect(writes(calls)[0]!.body).toEqual({ autoInvite: false });
  });

  it("shows the refusal when the switch cannot be saved and does not tell the page", async () => {
    serve({ "PUT /integrations/c1": { status: 403, body: { title: "forbidden" } } });
    const onChange = open();
    await userEvent.click(await screen.findByRole("checkbox", { name: /Invite Jira people/ }));
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
    expect(onChange).not.toHaveBeenCalled();
  });

  it("gives a person who is not an admin no invitation switch", async () => {
    serve();
    open({}, false);
    await screen.findByText(/Connected by/);
    expect(screen.queryByRole("checkbox", { name: /Invite Jira people/ })).not.toBeInTheDocument();
  });
});
