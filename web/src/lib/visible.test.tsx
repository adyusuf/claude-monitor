import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { VisibleText } from "../components/VisibleText";
import { escapeOf, hasInvisible, visibleSegments } from "./visible";

const flat = (text: string) => visibleSegments(text).map((s) => s.text).join("");

describe("visibleSegments", () => {
  it("leaves plain printable ASCII as one unflagged run", () => {
    expect(visibleSegments("ls -la /tmp ~ {x}")).toEqual([{ text: "ls -la /tmp ~ {x}", flagged: false }]);
    expect(visibleSegments("")).toEqual([]);
  });

  it("shows the right-to-left override only as its escape, flagged", () => {
    expect(visibleSegments("a‮b")).toEqual([
      { text: "a", flagged: false }, { text: "\\u202E", flagged: true }, { text: "b", flagged: false },
    ]);
  });

  it("shows zero-width characters, ESC, DEL and NUL as escapes", () => {
    expect(flat("​")).toBe("\\u200B");
    expect(flat("‍")).toBe("\\u200D");
    expect(flat("﻿")).toBe("\\uFEFF");
    expect(flat("\u001b[31m")).toBe("\\u001B[31m");
    expect(flat("\u007f")).toBe("\\u007F");
    expect(flat("a\u0000b")).toBe("a\\0b");
    expect(visibleSegments("\u001b")[0]!.flagged).toBe(true);
  });

  it("shows a combining mark by its escape alone, not as a glyph that would merge with its neighbour", () => {
    expect(visibleSegments("é")).toEqual([{ text: "e", flagged: false }, { text: "\\u0301", flagged: true }]);
  });

  it("shows a separator (no-break space, line separator) as its escape", () => {
    expect(flat("a b")).toBe("a\\u00A0b");
    expect(flat(" ")).toBe("\\u2028");
  });

  it("shows a visible non-ASCII character as glyph plus escape, flagged", () => {
    expect(visibleSegments("é")).toEqual([{ text: "é \\u00E9", flagged: true }]);
    expect(visibleSegments("а")).toEqual([{ text: "а \\u0430", flagged: true }]); // Cyrillic a
  });

  it("writes a code point above the BMP with braces, glyph kept", () => {
    expect(visibleSegments("😀")).toEqual([{ text: "😀 \\u{1F600}", flagged: true }]);
  });

  it("names tab, carriage return and NUL, and marks a newline so the page can break the line", () => {
    expect(visibleSegments("a\tb")).toEqual([{ text: "a", flagged: false }, { text: "\\t", flagged: true }, { text: "b", flagged: false }]);
    expect(flat("\r")).toBe("\\r");
    expect(visibleSegments("a\nb")).toEqual([
      { text: "a", flagged: false }, { text: "\\n", flagged: true, newline: true }, { text: "b", flagged: false },
    ]);
    expect(visibleSegments("\t")[0]).not.toHaveProperty("newline");
  });

  it("treats the boundaries of printable ASCII exactly (space and tilde plain, 0x1F and 0x7F not)", () => {
    expect(visibleSegments(" ~")).toEqual([{ text: " ~", flagged: false }]);
    expect(visibleSegments("\u001f")[0]).toEqual({ text: "\\u001F", flagged: true });
    expect(visibleSegments("\u007f")[0]).toEqual({ text: "\\u007F", flagged: true });
  });
});

describe("escapeOf", () => {
  it("pads a BMP code point to four hex digits and braces the rest", () => {
    expect(escapeOf(0x1)).toBe("\\u0001");
    expect(escapeOf(0xffff)).toBe("\\uFFFF");
    expect(escapeOf(0x10000)).toBe("\\u{10000}");
    expect(escapeOf(0x9)).toBe("\\t");
  });
});

describe("hasInvisible", () => {
  it("is false for plain ASCII, newlines, tabs, carriage returns and visible non-ASCII letters", () => {
    expect(hasInvisible("hello world\n\t\r")).toBe(false);
    expect(hasInvisible("çok güzel")).toBe(false);
    expect(hasInvisible("")).toBe(false);
  });

  it("is true for anything with no glyph", () => {
    for (const bad of ["‮", "​", "\u001b", "\u0000", "\u007f", "́", " ", " ", "﻿"]) {
      expect(hasInvisible(`x${bad}y`)).toBe(true);
    }
  });
});

describe("VisibleText", () => {
  it("renders plain text bare, flagged runs in a mark, and a real line break after the newline marker", () => {
    const { container } = render(<pre><VisibleText text={"a‮b\nc"} /></pre>);
    const marks = [...container.querySelectorAll("mark.vis-flag")].map((m) => m.textContent);
    expect(marks).toEqual(["\\u202E", "\\n"]);
    expect(container.querySelector("pre")!.textContent).toBe("a\\u202Eb\\n\nc");
  });

  it("never interprets markup in the text", () => {
    const { container } = render(<VisibleText text="<b>x</b>" />);
    expect(container.querySelector("b")).toBeNull();
    expect(container.textContent).toBe("<b>x</b>");
  });
});
