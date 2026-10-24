import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ConnectionView } from "../api/typesIntegrations";
import { renderAt } from "../test/helpers";
import { connection, localIso, serveIntegrations as serve, siteHeading, stubLocation, syncRun } from "../test/integrations-fixtures";
import { IntegrationCard } from "./IntegrationCard";

afterEach(() => vi.unstubAllGlobals());

const open = (over: Partial<ConnectionView> = {}, admin = true) => {
  const onChange = vi.fn();
  renderAt("/", [{ path: "/", element: <IntegrationCard connection={connection(over)} ws="w1" admin={admin} onChange={onChange} /> }]);
  return onChange;
};

describe("a connection card: how it stands", () => {
  it("shows the site, the green Connected badge, who connected it and how many projects came in", async () => {
    serve();
    open();
    const head = await screen.findByRole("heading", siteHeading("acme.atlassian.net"));
    expect(within(head).getByText("Connected")).toHaveClass("badge", "ok");
    expect(screen.getByText(/Connected by Örnek Kişi/)).toBeInTheDocument();
    expect(screen.getByText(/Projects imported: 2/)).toBeInTheDocument();
    expect(screen.queryByText(/must sign in again/)).not.toBeInTheDocument();
  });

  it("shows the Sign in again badge in warning colour and the needs-sign-in notice for a connection that needs it", async () => {
    serve();
    open({ status: "reauthorize" });
    const head = await screen.findByRole("heading", siteHeading("acme.atlassian.net"));
    expect(within(head).getByText("Sign in again")).toHaveClass("badge", "warn");
    expect(screen.getByRole("alert")).toHaveTextContent("The account that connected this site must sign in again.");
  });

  it("shows the date of the last check as dd/mm/yyyy hh:mm", async () => {
    serve();
    open({ lastPassAt: localIso(2026, 10, 3, 7, 5) });
    expect(await screen.findByText(/Last check 03\/10\/2026 07:05/)).toBeInTheDocument();
  });

  it("says it was not checked yet when there was no check", async () => {
    serve();
    open({ lastPassAt: null });
    expect(await screen.findByText(/Not checked yet/)).toBeInTheDocument();
    expect(screen.queryByText(/Last check/)).not.toBeInTheDocument();
  });

  it("says live updates are on until the webhook's end date, and off when it is not active", async () => {
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
    await screen.findByRole("heading", { level: 2, name: /acme/ });
    expect(screen.queryByText(/Last problem/)).not.toBeInTheDocument();
  });

  it("shows the recent checks from the API under the card", async () => {
    serve({ "GET /integrations/c1/runs": { body: [syncRun()] } });
    open();
    expect(await screen.findByText("Done")).toHaveClass("badge", "ok");
    expect(screen.getByText(/3 new · 4 updated · 1 archived · 2 people/)).toBeInTheDocument();
  });

  it("shows the empty text for the checks when they cannot be loaded", async () => {
    serve({ "GET /integrations/c1/runs": { status: 500 } });
    open();
    expect(await screen.findByText("Nothing has run yet.")).toBeInTheDocument();
  });
});

describe("a connection card: your own account", () => {
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
