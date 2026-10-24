import { describe, expect, it } from "vitest";
import { dictionaries } from "./index";

/** Every dotted path of a nested dictionary to its text. */
function leaves(node: unknown, prefix = ""): [string, unknown][] {
  if (typeof node === "object" && node !== null) return Object.entries(node).flatMap(([k, v]) => leaves(v, prefix ? `${prefix}.${k}` : k));
  return [[prefix, node]];
}
const of = (language: "en" | "tr" | "de") => leaves(dictionaries[language].integrations);
const placeholders = (text: unknown) => [...String(text).matchAll(/\{(\w+)\}/g)].map((m) => m[1]!).sort();

describe("the integrations texts", () => {
  it("have the same keys in English, Turkish and German", () => {
    const english = of("en").map(([k]) => k);
    expect(english).toEqual(expect.arrayContaining(["title", "result.site_not_found", "status.reauthorize", "sprintState.closed", "runStatus.partial", "runKind.webhook", "openInJira"]));
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

  it("have the navigation entry in all three languages", () => {
    for (const language of ["en", "tr", "de"] as const) {
      const nav = dictionaries[language].nav.integrations;
      expect(nav, language).toEqual(expect.any(String));
      expect(nav.trim(), language).not.toBe("");
    }
    expect(dictionaries.en.nav.integrations).toBe("Integrations");
  });

  it("have the error texts the pages use for the codes of the work tracker", () => {
    for (const language of ["en", "tr", "de"] as const) {
      for (const code of ["invalid_site", "integration_unavailable", "reauthorize", "tracker_unavailable", "space_not_found", "tracker_rate_limited", "internal_error"] as const) {
        expect(dictionaries[language].errors[code], `${language} ${code}`).toEqual(expect.any(String));
      }
    }
  });
});
