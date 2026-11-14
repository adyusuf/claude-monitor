import { describe, expect, it } from "vitest";
import { EXEC_LEVELS, EXEC_LEVEL_LIFETIME_MS, execLevelPayload, raises } from "./execLevel";

describe("which exec-level changes must be signed", () => {
  it.each([
    ["off", "argv", true],
    ["off", "shell", true],
    ["argv", "shell", true],
    ["argv", "off", false],
    ["shell", "argv", false],
    ["shell", "off", false],
    ["off", "off", false],
    ["argv", "argv", false],
    ["shell", "shell", false],
    [null, "argv", true],
    [undefined, "off", false],
    ["", "shell", true],
    ["root", "argv", true],
    ["shell", "root", false],
  ] as const)("going from %s to %s needs a signature: %s", (from, to, expected) => {
    expect(raises(from, to)).toBe(expected);
  });

  it("knows the three levels from the lowest to the highest", () => {
    expect(EXEC_LEVELS).toEqual(["off", "argv", "shell"]);
  });
});

describe("the payload that is signed for an exec-level change", () => {
  it("is the tag, the machine, the level, the sequence and the deadline in whole Unix seconds, one per line (the API signs and checks the same text)", () => {
    expect(execLevelPayload("11111111-2222-3333-4444-555555555555", "argv", 7, "2026-11-11T12:00:00.000Z"))
      .toBe("ap-exec-level-v1\n11111111-2222-3333-4444-555555555555\nargv\n7\n1794398400");
  });

  it("writes the machine id in lower case and drops fractions of a second", () => {
    const upper = execLevelPayload("AAAAAAAA-0000-0000-0000-000000000001", "shell", 1, "2026-11-11T12:00:00.999Z");
    const lower = execLevelPayload("aaaaaaaa-0000-0000-0000-000000000001", "shell", 1, "2026-11-11T12:00:00.000Z");
    expect(upper).toBe(lower);
  });

  it("asks for a lifetime the API accepts: five minutes, under its ten-minute ceiling", () => {
    expect(EXEC_LEVEL_LIFETIME_MS).toBe(5 * 60 * 1000);
  });
});
