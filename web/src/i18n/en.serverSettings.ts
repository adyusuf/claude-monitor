// The serverSettings namespace of the English dictionary: the server-wide integration settings (ADR-0017); en.ts assigns it as `serverSettings`.
export const enServerSettings = {
  title: "Server settings: integrations",
  intro: "Settings for the whole server. Values set on the server itself cannot be changed here.",
  noAccess: "This page is for the administrators of this server.",
  cannotSave: "The integration key is missing on the server, so nothing can be saved here. Set AIPOD_INTEGRATION_KEY on the server first.",
  names: { smtp: "Email (SMTP)", github: "GitHub sign-in", google: "Google sign-in", jira: "Jira" },
  status: { ready: "Ready", incomplete: "Incomplete", off: "Off" },
  statusLabel: "Status: {status}",
  notes: { integration_key_missing: "The integration key is missing on the server: Jira cannot keep its tokens until it is set." },
  fields: {
    host: "Server", port: "Port", user: "User name", password: "Password", from: "Sender address", tls: "Use STARTTLS",
    client_id: "Client ID", client_secret: "Client secret",
  },
  required: "required",
  setOnServer: "Set on the server ({env})",
  saved: "Saved", notSet: "Not set",
  secretKeep: "Leave empty to keep the saved value.",
  remove: "Remove", removeNamed: "Remove the saved {name}", removing: "Will be removed when you save.", undoRemove: "Keep it",
  callback: "Callback address", callbackHint: "Register this address at the provider.",
  copy: "Copy", copyNamed: "Copy the callback address of {name}", copied: "Copied", copyFailed: "The address could not be copied. Select it and copy it by hand.", hiddenSaved: "A value saved on this page is hidden by the server's. It would apply again if the server's were removed.", hiddenRemove: "Remove the saved value", hiddenRemoveNamed: "Remove the saved {name} hidden by the server's value", resetDefault: "Use the default", resetNamed: "Remove the saved {name} and use the default",
  save: "Save", saveNamed: "Save {name}", savedDone: "Saved.",
  testMail: "Send test mail", testMailHint: "Uses the saved settings and sends the mail to your own address.",
  testMailSent: "A test mail went to your address. Check your mailbox.",
  errors: {
    invalid_setting: "This integration does not have that setting.",
    invalid_value: "That value is not valid for this setting.",
  },
};
