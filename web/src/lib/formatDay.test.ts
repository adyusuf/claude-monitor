import { describe, expect, it } from "vitest";
import { isDay, parseDay } from "./format";

describe("typed calendar days (dd/mm/yyyy)", () => {
  it("turns dd/mm/yyyy into the API's yyyy-mm-dd", () => {
    expect(parseDay("05/10/2026")).toBe("2026-10-05");
    expect(parseDay(" 29/02/2028 ")).toBe("2028-02-29");
  });

  it("refuses what is not a real day or not that form", () => {
    for (const text of ["31/02/2026", "29/02/2026", "00/01/2026", "13/13/2026", "2026-10-05", "5/10/2026", "05.10.2026", "05/10/26", "", "abc"]) {
      expect(parseDay(text), text).toBeNull();
    }
  });

  it("knows a real yyyy-mm-dd day from a rolled-over one", () => {
    expect(isDay("2026-10-05")).toBe(true);
    expect(isDay("2026-02-31")).toBe(false);
    expect(isDay("05/10/2026")).toBe(false);
    expect(isDay("")).toBe(false);
  });
});
