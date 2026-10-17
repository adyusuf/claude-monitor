import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Markdown, safeHref } from "./markdown";

const html = (text: string) => render(<Markdown text={text} />).container;

describe("inline formatting", () => {
  it("renders bold, italic, strike and code, nested inside each other, and leaves snake_case and a lone star alone", () => {
    const c = html("**bold *and italic* text**, _under_, *star*, ~~gone~~, `code`, my_snake_case_name and 2 * 3 = 6");
    expect(c.querySelector("strong")?.textContent).toBe("bold and italic text");
    expect(c.querySelector("strong em")?.textContent).toBe("and italic");
    expect([...c.querySelectorAll("em")].map((e) => e.textContent)).toEqual(["and italic", "under", "star"]);
    expect(c.querySelector("del")?.textContent).toBe("gone");
    expect(c.querySelector("code")?.textContent).toBe("code");
    expect(c.textContent).toContain("my_snake_case_name");
    expect(c.textContent).toContain("2 * 3 = 6");
  });

  it("keeps what is inside code as it is and lets a backslash escape a mark", () => {
    const c = html("``a `tick` **not bold** b`` and \\*not italic\\* and \\# not a heading");
    expect(c.querySelector("code")?.textContent).toBe("a `tick` **not bold** b");
    expect(c.querySelector("em")).toBeNull();
    expect(c.textContent).toContain("*not italic*");
    expect(c.textContent).toContain("# not a heading");
  });

  it("keeps the line breaks the writer made", () => {
    const c = html("first line\nsecond line\n\nnew paragraph");
    expect(c.querySelectorAll("p")).toHaveLength(2);
    expect(c.querySelectorAll("p")[0]!.querySelectorAll("br")).toHaveLength(1);
  });
});

describe("links", () => {
  it("links http, https and mailto addresses, with a safe target, and not a javascript:, data: or relative one", () => {
    const c = html("[docs](https://example.com/a?b=1) [mail](mailto:a@b.invalid) [bad](javascript:alert(1)) [data](data:text/html,x) [rel](/etc/passwd)");
    const links = [...c.querySelectorAll("a")];
    expect(links.map((a) => a.getAttribute("href"))).toEqual(["https://example.com/a?b=1", "mailto:a@b.invalid"]);
    expect(links.every((a) => a.getAttribute("rel") === "noopener noreferrer nofollow" && a.getAttribute("target") === "_blank")).toBe(true);
    expect(c.textContent).toContain("[data](data:text/html,x)"); // shown as the text it was
  });

  it("links a bare address without the sentence's full stop, and an address in angle brackets", () => {
    const c = html("See https://example.com/path. Or <https://example.org/x> or (https://a.invalid/b).");
    expect([...c.querySelectorAll("a")].map((a) => a.textContent)).toEqual(["https://example.com/path", "https://example.org/x", "https://a.invalid/b"]);
    expect(c.textContent).toContain("path. Or");
  });

  it("only makes a link of an address the browser would take as http, https or mailto", () => {
    expect(safeHref("https://example.com")).toBe("https://example.com/");
    expect(safeHref("javascript:alert(1)")).toBeNull();
    expect(safeHref("//evil.invalid")).toBeNull();
    expect(safeHref("not a url")).toBeNull();
  });
});

describe("blocks", () => {
  it("nests lists, keeps the number a numbered list starts at and draws task lists with a checkbox", () => {
    const c = html("- one\n  - inner a\n  - inner b\n- two\n\n3. third\n4. fourth\n\n- [x] done\n- [ ] open");
    const outer = c.querySelector("ul")!;
    expect(outer.querySelectorAll(":scope > li")).toHaveLength(2);
    expect([...outer.querySelectorAll("li li")].map((l) => l.textContent)).toEqual(["inner a", "inner b"]);
    expect(c.querySelector("ol")?.getAttribute("start")).toBe("3");
    const boxes = [...c.querySelectorAll<HTMLInputElement>("li.md-task input[type=checkbox]")];
    expect(boxes.map((b) => [b.checked, b.disabled])).toEqual([[true, true], [false, true]]);
  });

  it("renders block quotes, which may hold lists and code, and rules", () => {
    const c = html("> quoted **text**\n> - item\n>\n> ```\n> code\n> ```\n\n---\n\nafter");
    const q = c.querySelector("blockquote")!;
    expect(q.querySelector("strong")?.textContent).toBe("text");
    expect(q.querySelectorAll("li")).toHaveLength(1);
    expect(q.querySelector("pre code")?.textContent).toBe("code");
    expect(c.querySelector("hr")).not.toBeNull();
  });

  it("renders a table with its alignment, an escaped bar in a cell and a short row padded", () => {
    const c = html("| Name | Qty | Note |\n|:-----|----:|:----:|\n| a \\| b | 3 | **ok** |\n| short | 1 |");
    expect([...c.querySelectorAll("th")].map((h) => [h.textContent, h.style.textAlign])).toEqual([["Name", "left"], ["Qty", "right"], ["Note", "center"]]);
    const rows = [...c.querySelectorAll("tbody tr")];
    expect(rows).toHaveLength(2);
    expect(rows[0]!.querySelectorAll("td")[0]!.textContent).toBe("a | b");
    expect(rows[0]!.querySelector("td strong")?.textContent).toBe("ok");
    expect(rows[1]!.querySelectorAll("td")).toHaveLength(3);
  });

  it("is not fooled by a pipe in prose, and a line of dashes is a rule not a table", () => {
    const c = html("a | b is just text\nnext line\n\n- - -");
    expect(c.querySelector("table")).toBeNull();
    expect(c.querySelector("hr")).not.toBeNull();
  });

  it("gives headings their level and bold look, and fenced code its language, with ~~~ fences and an unfinished block", () => {
    const c = html("# One\n### Three\n\n```ts\nconst a = 1;\n```\n\n~~~\ntilde\n~~~\n\n```\nnever closed");
    expect([...c.querySelectorAll("[role=heading]")].map((h) => [h.getAttribute("aria-level"), h.textContent])).toEqual([["3", "One"], ["5", "Three"]]);
    expect(c.querySelector("[role=heading] strong")).not.toBeNull();
    const pre = [...c.querySelectorAll("pre")];
    expect(pre.map((p) => p.getAttribute("data-lang"))).toEqual(["ts", null, null]);
    expect(pre.map((p) => p.textContent)).toEqual(["const a = 1;", "tilde", "never closed"]);
  });
});

describe("hostile and huge text", () => {
  it("never builds HTML from the text", () => {
    const c = html("<script>alert(1)</script> <img src=x onerror=alert(1)> [x](<javascript:1>) **<b>y</b>**");
    expect(c.querySelector("script, img, b")).toBeNull();
    expect(c.textContent).toContain("<script>alert(1)</script>");
    expect(c.textContent).toContain("<img src=x onerror=alert(1)>");
  });

  it("shows a very long run of text as it is, quickly, instead of scanning it for emphasis", () => {
    const wall = "**a ".repeat(3000);
    const started = performance.now();
    const c = html(wall);
    expect(performance.now() - started).toBeLessThan(1500);
    expect(c.textContent?.startsWith("**a **a")).toBe(true);
    expect(c.querySelector("strong")).toBeNull();
  });

  it("stops nesting quotes and lists at a depth instead of overflowing", () => {
    const quotes = html(`${"> ".repeat(40)}deep`);
    expect(quotes.textContent).toContain("deep");
    const lists = html(Array.from({ length: 30 }, (_, i) => `${"  ".repeat(i)}- level ${i}`).join("\n"));
    expect(lists.textContent).toContain("level 29");
  });
});
