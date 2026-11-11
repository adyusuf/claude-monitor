import { describe, expect, it } from "vitest";
import { safeNext } from "./session";

describe("safeNext, the guard on where sign-in sends the person afterwards", () => {
  it.each([
    ["/", "/"],
    ["/w/w1/sessions", "/w/w1/sessions"],
    ["/w/w1/sessions?tab=live#top", "/w/w1/sessions?tab=live#top"],
    ["/account", "/account"],
  ])("follows the same-site path %j", (next, expected) => {
    expect(safeNext(next)).toBe(expected);
  });

  it.each([
    ["a missing address", null],
    ["an empty address", ""],
    ["a protocol-relative address", "//evil.example.net"],
    ["a protocol-relative address with a path", "//evil.example.net/w/w1"],
    ["an absolute https address", "https://evil.example.net"],
    ["an absolute http address", "http://evil.example.net/w/w1"],
    ["a javascript address", "javascript:alert(1)"],
    ["a path with no leading slash", "w/w1/sessions"],
  ])("falls back to the start page for %s", (_label, next) => {
    expect(safeNext(next)).toBe("/");
  });
});
