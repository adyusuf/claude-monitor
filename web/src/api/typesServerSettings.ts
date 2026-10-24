// The API's shapes for the server-wide settings page (ADR-0017), as ServerSettingsViews.cs sends them. The id, status, source and
// field name are kept as text: a newer server may send a value this web does not know yet, and the page shows it as it is.

/** The integrations this web has texts for; any other id is shown by its raw name. */
export const INTEGRATION_IDS = ["smtp", "github", "google", "jira"] as const;
export type IntegrationId = (typeof INTEGRATION_IDS)[number];

/** Ready, incomplete (a required value is missing) or off (nothing set); any other status is shown as it is. */
export const IntegrationStatus = { ready: "ready", incomplete: "incomplete", off: "off" } as const;
export type IntegrationStatusValue = (typeof IntegrationStatus)[keyof typeof IntegrationStatus];
export const INTEGRATION_STATUSES: readonly IntegrationStatusValue[] = Object.values(IntegrationStatus);

/** Where a value comes from: the server's own environment (read-only here), this page, or nowhere yet. */
export const FieldSource = { server: "server", page: "page", none: "none" } as const;
export type FieldSourceValue = (typeof FieldSource)[keyof typeof FieldSource];

/** The settings this web has labels for; any other name is shown as it is. */
export const Setting = {
  host: "host", port: "port", user: "user", password: "password", from: "from", tls: "tls", clientId: "client_id", clientSecret: "client_secret",
} as const;
export type SettingName = (typeof Setting)[keyof typeof Setting];
export const SETTING_NAMES: readonly SettingName[] = Object.values(Setting);
/** How a switch such as STARTTLS travels: as text. */
export const SwitchValue = { on: "true", off: "false" } as const;

/** The id of the one integration that can send a test mail. */
export const SMTP_ID: IntegrationId = "smtp";

export interface SettingFieldView {
  name: string;
  /** The environment variable that sets it on the server, such as AIPOD_SMTP_PASSWORD. */
  envName: string;
  secret: boolean;
  required: boolean;
  source: string;
  /** Always null for a secret. */
  value: string | null;
  /** A value saved on the page that the environment's hides; it comes back if the environment's goes. Missing on an older server. */
  savedHidden?: boolean;
}

export interface IntegrationSettingsView {
  id: string;
  status: string;
  /** The address to register at the provider; null when the integration has none. */
  callbackUrl: string | null;
  /** Codes such as integration_key_missing. */
  notes: string[];
  fields: SettingFieldView[];
}

export interface ServerIntegrationsView {
  canSave: boolean;
  integrations: IntegrationSettingsView[];
}

/** What a save sends: an empty value removes the saved one; a secret left out stays as it is. */
export interface ServerSettingsInput {
  values: Record<string, string | null>;
  code?: string;
}
