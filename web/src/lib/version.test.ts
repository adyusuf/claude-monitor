import { describe, expect, it } from "vitest";
import { versionAtLeast } from "./version";

describe("versionAtLeast", () => {
  it("accepts the same version", () => {
    expect(versionAtLeast("0.5.0", "0.5.0")).toBe(true);
  });

  it("compares the patch, the minor and the major part in that weight", () => {
    expect(versionAtLeast("0.5.1", "0.5.0")).toBe(true);
    expect(versionAtLeast("0.5.0", "0.5.1")).toBe(false);
    expect(versionAtLeast("0.6.0", "0.5.9")).toBe(true);
    expect(versionAtLeast("0.4.9", "0.5.0")).toBe(false);
    expect(versionAtLeast("1.0.0", "0.9.9")).toBe(true);
    expect(versionAtLeast("0.9.9", "1.0.0")).toBe(false);
  });

  it("compares numbers, not text (0.10.0 is above 0.9.0)", () => {
    expect(versionAtLeast("0.10.0", "0.9.0")).toBe(true);
    expect(versionAtLeast("0.9.0", "0.10.0")).toBe(false);
  });

  it("treats a missing part as zero when the two have different lengths", () => {
    expect(versionAtLeast("0.5", "0.5.0")).toBe(true);
    expect(versionAtLeast("0.5.0", "0.5")).toBe(true);
    expect(versionAtLeast("0.5", "0.5.1")).toBe(false);
    expect(versionAtLeast("0.5.0.1", "0.5.0")).toBe(true);
    expect(versionAtLeast("0.5.0.0", "0.5.0.1")).toBe(false);
  });

  it("counts a missing version as lower", () => {
    expect(versionAtLeast(null, "0.5.0")).toBe(false);
    expect(versionAtLeast(undefined, "0.5.0")).toBe(false);
    expect(versionAtLeast("", "0.5.0")).toBe(false);
  });

  it("counts anything that is not numbers and dots as lower, even when the numbers would be high enough", () => {
    expect(versionAtLeast("garbage", "0.5.0")).toBe(false);
    expect(versionAtLeast("1.2.3-beta", "0.5.0")).toBe(false);
    expect(versionAtLeast("v1.2.3", "0.5.0")).toBe(false);
    expect(versionAtLeast("1..2", "0.5.0")).toBe(false);
    expect(versionAtLeast("1.2.", "0.5.0")).toBe(false);
    expect(versionAtLeast(" 1.2.3", "0.5.0")).toBe(false);
  });

  it("counts a minimum that is not a version as not met", () => {
    expect(versionAtLeast("1.0.0", "garbage")).toBe(false);
  });
});
