import { describe, expect, it } from "vitest";
import { fieldLabel, noticeLine, parseNotice } from "./notice";

const TASK = `<task-notification>
<task-id>b1e4gny2p</task-id>
<tool-use-id>toolu_01MF9A8NHqV8WYhyRSA6K3cz</tool-use-id>
<output-file>C:\\Users\\Example\\AppData\\Local\\Temp\\claude\\tasks\\b1e4gny2p.output</output-file>
<status>completed</status>
<summary>Background command "Run admin lint, typecheck and full test suite" completed (exit code 0)</summary>
</task-notification>`;

describe("reading a tagged notice", () => {
  it("turns a task notification into its fields, in order, without the tags", () => {
    const n = parseNotice(TASK)!;
    expect(n.tag).toBe("task-notification");
    expect(n.fields.map((f) => f.name)).toEqual(["task-id", "tool-use-id", "output-file", "status", "summary"]);
    expect(n.fields.find((f) => f.name === "status")?.value).toBe("completed");
    expect(n.fields.find((f) => f.name === "output-file")?.value).toBe("C:\\Users\\Example\\AppData\\Local\\Temp\\claude\\tasks\\b1e4gny2p.output");
  });

  it("decodes the characters XML writes as entities", () => {
    const n = parseNotice("<note><summary>a &lt;b&gt; &amp; &quot;c&quot; &apos;d&apos;</summary></note>")!;
    expect(n.fields[0]!.value).toBe("a <b> & \"c\" 'd'");
  });

  it("keeps several top-level elements and the text around them", () => {
    const n = parseNotice("<bash-input>ls</bash-input>\n<bash-stdout>a\nb</bash-stdout> and then more")!;
    expect(n.fields).toEqual([{ name: "bash-input", value: "ls" }, { name: "bash-stdout", value: "a\nb" }, { name: "text", value: "and then more" }]);
  });

  it("keeps the text of an element with no elements inside, and of a tag that is never closed", () => {
    expect(parseNotice("<reminder>Remember Ş, ğ, ı, İ.</reminder>")!.fields).toEqual([{ name: "text", value: "Remember Ş, ğ, ı, İ." }]);
    expect(parseNotice("<oops>no closing tag")!.fields).toEqual([{ name: "text", value: "<oops>no closing tag" }]);
  });

  it("is null for text that is not tagged", () => {
    expect(parseNotice("fix the login")).toBeNull();
    expect(parseNotice("2 < 3 and <b> is not a tag at the start")).toBeNull();
    expect(parseNotice("")).toBeNull();
  });

  it("drops fields that are empty", () => {
    expect(parseNotice("<x><a></a><b>kept</b></x>")!.fields).toEqual([{ name: "b", value: "kept" }]);
  });
});

describe("the closed line of a notice", () => {
  it("says what it is, its status and its summary", () => {
    expect(noticeLine(parseNotice(TASK)!)).toEqual({
      label: "Task notification", status: "completed",
      summary: "Background command \"Run admin lint, typecheck and full test suite\" completed (exit code 0)",
    });
  });

  it("uses the first line of the first field that is not an id or a path when there is no summary", () => {
    const n = parseNotice("<job><job-id>7</job-id><output-file>/x</output-file><detail>first\nsecond</detail></job>")!;
    expect(noticeLine(n)).toEqual({ label: "Job", status: "", summary: "first" });
  });

  it("is empty-handed but does not fail for a notice with nothing in it", () => {
    expect(noticeLine({ tag: "empty", fields: [] })).toEqual({ label: "Empty", status: "", summary: "" });
  });

  it("names fields in words", () => {
    expect(fieldLabel("tool-use-id")).toBe("Tool use id");
    expect(fieldLabel("status")).toBe("Status");
  });
});
