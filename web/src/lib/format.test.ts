import { describe, expect, it } from "vitest";
import { date, dateTime, dateTimeSeconds, time } from "./format";

describe("format of an unparseable date", () => {
  it.each([["not a date"], ["2026-13-45"], ["NaN"]])("shows nothing for %s instead of NaN/NaN/NaN", (text) => {
    expect(date(text)).toBe("");
    expect(dateTime(text)).toBe("");
    expect(time(text)).toBe("");
    expect(dateTimeSeconds(text)).toBe("");
  });

  it("still writes a valid moment as before", () => {
    const iso = new Date(2026, 9, 3, 7, 5, 9).toISOString();
    expect(date(iso)).toBe("03/10/2026");
    expect(dateTime(iso)).toBe("03/10/2026 07:05");
    expect(time(iso)).toBe("07:05:09");
    expect(dateTimeSeconds(iso)).toBe("03/10/2026 07:05:09");
  });
});
