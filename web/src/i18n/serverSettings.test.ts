import { describe, expect, it } from "vitest";
import { dictionaries } from "./index";

/** Every dotted path of a nested dictionary to its text. */
function leaves(node: unknown, prefix = ""): [string, unknown][] {
  if (typeof node === "object" && node !== null) return Object.entries(node).flatMap(([k, v]) => leaves(v, prefix ? `${prefix}.${k}` : k));
  return [[prefix, node]];
}
const of = (language: "en" | "tr" | "de") => leaves(dictionaries[language].serverSettings);
const placeholders = (text: unknown) => [...String(text).matchAll(/\{(\w+)\}/g)].map((m) => m[1]!).sort();

describe("the server settings texts", () => {
  it("have the same keys in English, Turkish and German", () => {
    const english = of("en").map(([k]) => k);
    expect(english).toEqual(expect.arrayContaining(["title", "names.smtp", "status.incomplete", "notes.integration_key_missing", "fields.client_secret", "errors.invalid_value", "setOnServer"]));
    expect(of("tr").map(([k]) => k).sort()).toEqual([...english].sort());
    expect(of("de").map(([k]) => k).sort()).toEqual([...english].sort());
  });

  it.each(["en", "tr", "de"] as const)("have no empty or non-text value in %s", (language) => {
    for (const [key, value] of of(language)) {
      expect(typeof value, key).toBe("string");
      expect((value as string).trim(), key).not.toBe("");
    }
  });

  it.each(["tr", "de"] as const)("keep the {placeholders} of every English text in %s", (language) => {
    const english = new Map(of("en"));
    for (const [key, value] of of(language)) expect(placeholders(value), key).toEqual(placeholders(english.get(key)));
  });

  it.each(["tr", "de"] as const)("are translated in %s, not copied from English", (language) => {
    const english = new Map(of("en"));
    // Names that read the same in every language (Jira, Port, Server, Status: {status}) are allowed to match; the sentences are not.
    for (const key of ["title", "intro", "noAccess", "cannotSave", "setOnServer", "secretKeep", "callbackHint", "testMailSent", "errors.invalid_value"]) {
      expect(new Map(of(language)).get(key), `${language} ${key}`).not.toBe(english.get(key));
    }
  });

  it("have the navigation entry in all three languages", () => {
    for (const language of ["en", "tr", "de"] as const) {
      const nav = dictionaries[language].nav.serverSettings;
      expect(nav, language).toEqual(expect.any(String));
      expect(nav.trim(), language).not.toBe("");
    }
    expect(dictionaries.en.nav.serverSettings).toBe("Server settings");
  });

  it("have the error texts the page uses for the codes of the server settings", () => {
    for (const language of ["en", "tr", "de"] as const) {
      for (const code of ["set_on_server", "integration_key_missing", "mail_failed", "no_email", "invalid_setting", "invalid_value", "reauth_required", "invalid_code"] as const) {
        expect(dictionaries[language].errors[code], `${language} ${code}`).toEqual(expect.any(String));
      }
      for (const code of ["invalid_setting", "invalid_value"] as const) {
        expect(dictionaries[language].serverSettings.errors[code], `${language} ${code}`).toEqual(expect.any(String));
      }
    }
  });
});
