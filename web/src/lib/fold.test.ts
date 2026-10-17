import { describe, expect, it } from "vitest";
import { isOpen, withOpen } from "./fold";

describe("which folds are open", () => {
  it("is open while any line in it is, so lines joining it or leaving it do not close it", () => {
    const open = withOpen(new Set(), ["a", "b"], true);
    expect(isOpen(open, ["a", "b"])).toBe(true);
    expect(isOpen(open, ["b", "c"])).toBe(true); // c joined, a left
    expect(isOpen(open, ["c", "d"])).toBe(false);
  });

  it("closes by taking out the lines it holds now", () => {
    const open = withOpen(withOpen(new Set(), ["a", "b"], true), ["x"], true);
    const closed = withOpen(open, ["a", "b", "c"], false);
    expect(isOpen(closed, ["a", "b", "c"])).toBe(false);
    expect(isOpen(closed, ["x"])).toBe(true);
  });

  it("returns the same set when nothing changes, so the page is not drawn again", () => {
    const open = withOpen(new Set(), ["a"], true);
    expect(withOpen(open, ["a"], true)).toBe(open);
    expect(withOpen(open, ["z"], false)).toBe(open);
  });

  it("does not change the set it was given", () => {
    const before = new Set(["a"]);
    withOpen(before, ["b"], true);
    expect([...before]).toEqual(["a"]);
  });
});
