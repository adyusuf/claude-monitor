import { describe, expect, it } from "vitest";
import { field, smtp, smtpHidden } from "../test/server-settings-fixtures";
import { changedValues, initialDraft } from "./serverSettings";

/** E-mail with a saved user, a saved password, a STARTTLS saved on the page, a host set on the server and an unset sender. */
const mail = () => smtp({
  fields: [
    field({ name: "host", envName: "AIPOD_SMTP_HOST", source: "server", value: "relay.acme.test" }),
    field({ name: "user", envName: "AIPOD_SMTP_USER", source: "page", value: "mailer" }),
    field({ name: "password", envName: "AIPOD_SMTP_PASSWORD", secret: true, source: "page", value: null }),
    field({ name: "from", envName: "AIPOD_SMTP_FROM", source: "none", value: null }),
    field({ name: "tls", envName: "AIPOD_SMTP_TLS", required: false, source: "page", value: "false" }),
  ],
});
const none = new Set<string>();

describe("changedValues", () => {
  it("sends nothing for an untouched card", () => {
    const i = mail();
    expect(changedValues(i, initialDraft(i), none)).toEqual({});
  });

  it("sends an empty value for a saved setting marked for removal, whatever is typed", () => {
    const i = mail();
    expect(changedValues(i, { ...initialDraft(i), tls: "true" }, new Set(["tls"]))).toEqual({ tls: "" });
    expect(changedValues(i, initialDraft(i), new Set(["user"]))).toEqual({ user: "" });
  });

  it("sends an empty value for a saved secret marked for removal, even with a secret typed", () => {
    const i = mail();
    expect(changedValues(i, { ...initialDraft(i), password: "typed" }, new Set(["password"]))).toEqual({ password: "" });
  });

  it("sends a typed secret and leaves an empty one out", () => {
    const i = mail();
    expect(changedValues(i, { ...initialDraft(i), password: "n3w-pass" }, none)).toEqual({ password: "n3w-pass" });
    expect(changedValues(i, { ...initialDraft(i), password: "" }, none)).toEqual({});
  });

  it("sends a changed setting, and an emptied one as an empty value", () => {
    const i = mail();
    expect(changedValues(i, { ...initialDraft(i), user: "mailer2", from: "podium@acme.test" }, none)).toEqual({ user: "mailer2", from: "podium@acme.test" });
    expect(changedValues(i, { ...initialDraft(i), user: "" }, none)).toEqual({ user: "" });
  });

  it("leaves a setting typed back to its saved value out", () => {
    const i = mail();
    expect(changedValues(i, { ...initialDraft(i), user: "mailer", tls: "false" }, none)).toEqual({});
  });

  it("never sends a value set on the server, even when typed or marked for removal", () => {
    const i = mail();
    expect(changedValues(i, { ...initialDraft(i), host: "other.acme.test" }, none)).toEqual({});
    expect(changedValues(i, initialDraft(i), new Set(["host"]))).toEqual({});
  });

  it("never sends a field of a source it does not know", () => {
    const i = smtp({ fields: [field({ name: "region", envName: "AIPOD_X_REGION", source: "vault", value: "eu" })] });
    expect(changedValues(i, { region: "us" }, new Set(["region"]))).toEqual({});
  });
});

describe("changedValues for a value saved on the page that the server's hides", () => {
  it("sends an empty value for a hidden one marked for removal, a setting or a secret", () => {
    const i = smtpHidden();
    expect(changedValues(i, initialDraft(i), new Set(["host"]))).toEqual({ host: "" });
    expect(changedValues(i, initialDraft(i), new Set(["password"]))).toEqual({ password: "" });
  });

  it("leaves a hidden one out while it is not marked, even with something typed for it", () => {
    const i = smtpHidden();
    expect(changedValues(i, { ...initialDraft(i), host: "other.acme.test", password: "typed" }, none)).toEqual({});
  });

  it("never sends a server value that hides nothing, false or missing, even marked for removal", () => {
    const i = smtpHidden();
    expect(changedValues(i, initialDraft(i), new Set(["user"]))).toEqual({});
    expect(changedValues(i, initialDraft(i), new Set(["from"]))).toEqual({});
    const shown = smtpHidden(false);
    expect(changedValues(shown, initialDraft(shown), new Set(["host", "password"]))).toEqual({});
  });
});
