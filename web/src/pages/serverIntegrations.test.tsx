import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App } from "../App";
import { Layout } from "../components/Layout";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { field, github, jira, ORIGIN, serveServerSettings, serverView, smtp } from "../test/server-settings-fixtures";
import { ServerIntegrationsPage } from "./ServerIntegrationsPage";

const s = en.serverSettings;
const page = () => renderAt("/server/integrations", [{ path: "/server/integrations", element: <ServerIntegrationsPage /> }]);
/** The card of an integration, found by its heading. */
const card = async (name: string) => within((await screen.findByRole("heading", { level: 2, name })).closest("section")!);

describe("the server settings link in the frame", () => {
  it("shows the link to a server admin, pointing at the page", async () => {
    mockApi({ "GET /me": { body: { ...ME, serverAdmin: true } } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    expect(await screen.findByRole("link", { name: new RegExp(en.nav.serverSettings) })).toHaveAttribute("href", "/server/integrations");
  });

  it.each([["false", { ...ME, serverAdmin: false }], ["missing (an older server)", ME]])("hides the link when serverAdmin is %s", async (_, me) => {
    mockApi({ "GET /me": { body: me } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    expect(await screen.findByRole("link", { name: new RegExp(en.nav.integrations) })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: new RegExp(en.nav.serverSettings) })).not.toBeInTheDocument();
  });

  it("opens the page at /server/integrations inside the signed-in frame", async () => {
    serveServerSettings();
    renderAt("/server/integrations", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { level: 1, name: s.title })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { level: 2, name: s.names.smtp })).toBeInTheDocument();
  });
});

describe("the server settings page", () => {
  it("shows one card per integration with its status as text, its notes and the intro", async () => {
    serveServerSettings();
    page();
    expect(await screen.findByText(s.intro)).toBeInTheDocument();
    const mail = await card(s.names.smtp);
    expect(mail.getByText(s.statusLabel.replace("{status}", s.status.ready))).toHaveClass("badge", "ok");
    const gh = await card(s.names.github);
    expect(gh.getByText(s.statusLabel.replace("{status}", s.status.incomplete))).toHaveClass("badge", "warn");
    const j = await card(s.names.jira);
    expect(j.getByText(s.statusLabel.replace("{status}", s.status.off))).toHaveClass("badge");
    expect(j.getByText(s.notes.integration_key_missing)).toBeInTheDocument();
    expect(screen.queryByText(s.cannotSave)).not.toBeInTheDocument();
  });

  it("shows the callback address with its hint and copies it; e-mail has none", async () => {
    const user = userEvent.setup();
    serveServerSettings();
    page();
    const gh = await card(s.names.github);
    expect(gh.getByText(`${ORIGIN}/api/auth/external/callback/github`)).toBeInTheDocument();
    expect(gh.getByText(s.callbackHint)).toBeInTheDocument();
    await user.click(gh.getByRole("button", { name: s.copyNamed.replace("{name}", s.names.github) }));
    expect(await navigator.clipboard.readText()).toBe(`${ORIGIN}/api/auth/external/callback/github`);
    expect(gh.getByRole("button", { name: s.copyNamed.replace("{name}", s.names.github) })).toHaveTextContent(s.copied);
    expect((await card(s.names.smtp)).queryByText(s.callback)).not.toBeInTheDocument();
  });

  it("shows a value set on the server read-only with its variable, and a server secret without any input", async () => {
    serveServerSettings();
    page();
    const host = (await card(s.names.smtp)).getByLabelText(`${s.fields.host} (${s.required})`);
    expect(host).toHaveValue("relay.acme.test");
    expect(host).toHaveAttribute("readonly");
    expect((await card(s.names.smtp)).getByText(s.setOnServer.replace("{env}", "AIPOD_SMTP_HOST"))).toBeInTheDocument();
    const j = await card(s.names.jira);
    expect(j.getByText(s.setOnServer.replace("{env}", "AIPOD_JIRA_CLIENT_SECRET"))).toBeInTheDocument();
    expect(j.queryByLabelText(new RegExp(s.fields.client_secret))).not.toBeInTheDocument();
  });

  it("never fills a secret in: a saved one says saved, a missing one not set, and only a saved one can be removed", async () => {
    serveServerSettings();
    page();
    const mail = await card(s.names.smtp);
    const password = mail.getByLabelText(`${s.fields.password} (${s.required})`);
    expect(password).toHaveAttribute("type", "password");
    expect(password).toHaveValue("");
    expect(mail.getByText(`${s.saved} · ${s.secretKeep}`)).toBeInTheDocument();
    expect(mail.getByRole("button", { name: s.removeNamed.replace("{name}", s.fields.password) })).toBeEnabled();
    const gh = await card(s.names.github);
    expect(gh.getByLabelText(`${s.fields.client_secret} (${s.required})`)).toHaveValue("");
    expect(gh.getByText(`${s.notSet} · ${s.secretKeep}`)).toBeInTheDocument();
    expect(gh.queryByRole("button", { name: new RegExp(s.remove) })).not.toBeInTheDocument();
  });

  it("prefills the other settings: a number for the port, a switch for STARTTLS, required ones marked", async () => {
    serveServerSettings();
    page();
    const mail = await card(s.names.smtp);
    const port = mail.getByLabelText(`${s.fields.port} (${s.required})`);
    expect(port).toHaveAttribute("type", "number");
    expect(port).toHaveValue(587);
    expect(mail.getByLabelText(`${s.fields.from} (${s.required})`)).toHaveValue("podium@acme.test");
    expect(mail.getByRole("checkbox", { name: s.fields.tls })).toBeChecked();
  });

  it("shows an integration, status, note, field and source it does not know as they are, and sends only what can be sent", async () => {
    const future = {
      id: "slack", status: "paused", callbackUrl: null, notes: ["future_note"],
      fields: [field({ name: "webhook_url", envName: "AIPOD_SLACK_URL", value: "hook" }), field({ name: "region", envName: "AIPOD_SLACK_REGION", source: "vault", value: "eu" })],
    };
    const calls = serveServerSettings({
      "GET /server/integrations": { body: serverView({ integrations: [future] }) },
      "PUT /server/integrations/slack": { body: serverView({ integrations: [future] }) },
    });
    page();
    const c = await card("slack");
    const badge = c.getByText(s.statusLabel.replace("{status}", "paused"));
    expect(badge).toHaveClass("badge");
    expect(badge).not.toHaveClass("ok");
    expect(badge).not.toHaveClass("warn");
    expect(c.getByText("future_note")).toBeInTheDocument();
    const region = c.getByLabelText("region (required)");
    expect(region).toHaveAttribute("readonly");
    expect(c.getByText("vault")).toBeInTheDocument();
    await userEvent.clear(c.getByLabelText("webhook_url (required)"));
    await userEvent.type(c.getByLabelText("webhook_url (required)"), "next");
    await userEvent.click(c.getByRole("button", { name: s.saveNamed.replace("{name}", "slack") }));
    expect(await c.findByText(s.savedDone)).toBeInTheDocument();
    expect(calls.find((x) => x.method === "PUT")?.body).toEqual({ values: { webhook_url: "next" } });
  });

  it("says the page is not for this person when the API answers 404", async () => {
    serveServerSettings({ "GET /server/integrations": { status: 404, body: { title: "not_found" } } });
    page();
    expect(await screen.findByText(s.noAccess)).toBeInTheDocument();
    expect(screen.queryByRole("heading", { level: 2 })).not.toBeInTheDocument();
  });

  it("shows any other failure to load as an error", async () => {
    serveServerSettings({ "GET /server/integrations": { status: 500 } });
    page();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.queryByText(s.noAccess)).not.toBeInTheDocument();
  });

  it("keeps the cards in the order the server sends them", async () => {
    serveServerSettings({ "GET /server/integrations": { body: serverView({ integrations: [jira(), smtp(), github()] }) } });
    page();
    await screen.findByRole("heading", { level: 2, name: s.names.jira });
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual([s.names.jira, s.names.smtp, s.names.github]);
  });
});
