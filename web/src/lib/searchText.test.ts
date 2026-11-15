import { describe, expect, it } from "vitest";
import { normalizeSearch } from "./searchText";

describe("normalizeSearch", () => {
  it("folds case, accents and the Turkish dotted and dotless i like the API", () => {
    expect(normalizeSearch("İstanbul")).toBe("istanbul");
    expect(normalizeSearch("ISTANBUL")).toBe("istanbul");
    expect(normalizeSearch("ıspanak")).toBe("ispanak");
    expect(normalizeSearch("Şişman")).toBe("sisman");
    expect(normalizeSearch("  Café ")).toBe("cafe");
  });
});
