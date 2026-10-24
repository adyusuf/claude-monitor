import type { IntegrationSettingsView, ServerIntegrationsView, SettingFieldView } from "../api/typesServerSettings";
import { ME, mockApi } from "./helpers";

type Routes = Parameters<typeof mockApi>[0];

/** The origin the fixtures' callback addresses start with: a reserved test name, never a real host. */
export const ORIGIN = "https://podium.acme.test";

export const field = (over: Partial<SettingFieldView> = {}): SettingFieldView => ({
  name: "host", envName: "AIPOD_SMTP_HOST", secret: false, required: true, source: "page", value: "mail.acme.test", ...over,
});

/** E-mail as a server that set the host itself and keeps a password saved on the page. */
export const smtp = (over: Partial<IntegrationSettingsView> = {}): IntegrationSettingsView => ({
  id: "smtp", status: "ready", callbackUrl: null, notes: [],
  fields: [
    field({ name: "host", envName: "AIPOD_SMTP_HOST", source: "server", value: "relay.acme.test" }),
    field({ name: "port", envName: "AIPOD_SMTP_PORT", source: "page", value: "587" }),
    field({ name: "user", envName: "AIPOD_SMTP_USER", source: "page", value: "mailer" }),
    field({ name: "password", envName: "AIPOD_SMTP_PASSWORD", secret: true, source: "page", value: null }),
    field({ name: "from", envName: "AIPOD_SMTP_FROM", source: "none", value: "podium@acme.test" }),
    field({ name: "tls", envName: "AIPOD_SMTP_TLS", required: false, source: "none", value: "true" }),
  ],
  ...over,
});

/** E-mail as a server whose own host and password hide values saved on the page (savedHidden as given), while its user and sender,
 * also set on the server, hide nothing: false and missing (an older server). The port is saved on the page. */
export const smtpHidden = (savedHidden = true): IntegrationSettingsView => smtp({
  fields: [
    field({ name: "host", envName: "AIPOD_SMTP_HOST", source: "server", value: "relay.acme.test", savedHidden }),
    field({ name: "password", envName: "AIPOD_SMTP_PASSWORD", secret: true, source: "server", value: null, savedHidden }),
    field({ name: "user", envName: "AIPOD_SMTP_USER", source: "server", value: "mailer", savedHidden: false }),
    field({ name: "from", envName: "AIPOD_SMTP_FROM", source: "server", value: "podium@acme.test" }),
    field({ name: "port", envName: "AIPOD_SMTP_PORT", source: "page", value: "587" }),
  ],
});

export const github = (over: Partial<IntegrationSettingsView> = {}): IntegrationSettingsView => ({
  id: "github", status: "incomplete", callbackUrl: `${ORIGIN}/api/auth/external/callback/github`, notes: [],
  fields: [
    field({ name: "client_id", envName: "AIPOD_GITHUB_CLIENT_ID", source: "page", value: "Iv1.abc" }),
    field({ name: "client_secret", envName: "AIPOD_GITHUB_CLIENT_SECRET", secret: true, source: "none", value: null }),
  ],
  ...over,
});

export const jira = (over: Partial<IntegrationSettingsView> = {}): IntegrationSettingsView => ({
  id: "jira", status: "off", callbackUrl: `${ORIGIN}/api/integrations/jira/callback`, notes: ["integration_key_missing"],
  fields: [
    field({ name: "client_id", envName: "AIPOD_JIRA_CLIENT_ID", source: "none", value: null }),
    field({ name: "client_secret", envName: "AIPOD_JIRA_CLIENT_SECRET", secret: true, source: "server", value: null }),
  ],
  ...over,
});

export const serverView = (over: Partial<ServerIntegrationsView> = {}): ServerIntegrationsView => ({
  canSave: true, integrations: [smtp(), github(), jira()], ...over,
});

/** The calls the server settings page makes; a test overrides what it cares about. */
export const serveServerSettings = (over: Routes = {}) => mockApi({
  "GET /me": { body: { ...ME, serverAdmin: true } },
  "GET /server/integrations": { body: serverView() },
  ...over,
});
