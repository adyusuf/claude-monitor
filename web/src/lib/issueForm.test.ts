import { describe, expect, it } from "vitest";
import { issueView } from "../test/issues-fixtures";
import { project } from "../test/projects-fixtures";
import { checkIssue, codeLinkUrlOk, draftOfIssue, inputOfIssue } from "./issueForm";
import { estimateOk } from "./issueOptions";

describe("the issue form helpers", () => {
  it("turns a view into a draft and back into the same input", () => {
    const draft = draftOfIssue(issueView());
    expect(draft).toMatchObject({ title: "Pay with a card", priority: "must", estimate: "5", assigneeUserId: "u2", parentId: "", severity: "" });
    expect(inputOfIssue(draft)).toMatchObject({ title: "Pay with a card", priority: "must", estimate: "5", parentId: null, applicationId: null });
  });

  it("sends no component or release without an application, and nothing of a bug for another type", () => {
    const draft = { ...draftOfIssue(issueView()), componentId: "c1", releaseId: "r1", bugEnvironment: "x", severity: "major" as const };
    expect(inputOfIssue(draft)).toMatchObject({ componentId: null, releaseId: null, bugEnvironment: null, severity: null });
    expect(inputOfIssue({ ...draft, applicationId: "a1", type: "bug" })).toMatchObject({ componentId: "c1", releaseId: "r1", bugEnvironment: "x", severity: "major" });
  });

  it("reports no problem for a good draft and the first problem of each field otherwise", () => {
    expect(checkIssue(draftOfIssue(issueView()), project())).toEqual({});
    const bad = { ...draftOfIssue(issueView()), title: " ", priority: "p0", estimate: "abc" };
    expect(checkIssue(bad, project())).toEqual({ title: "invalid_name", priority: "invalid_value", estimate: "invalid_value" });
  });

  it("knows which estimates a unit allows", () => {
    expect(["0", "5", "12.5", "9999.99", "0.25"].every((v) => estimateOk(v, "hours"))).toBe(true);
    expect(["", "-1", "1.234", "10000", "1e3", "a", "1,5"].some((v) => estimateOk(v, "days"))).toBe(false);
    expect(estimateOk("xl", "t_shirt")).toBe(true);
    expect(estimateOk("5", "t_shirt")).toBe(false);
  });

  it("accepts only web addresses without user information within the length", () => {
    expect(codeLinkUrlOk("https://git.example.org/pr/1")).toBe(true);
    expect(codeLinkUrlOk("http://git.example.org/pr/1")).toBe(true);
    expect(codeLinkUrlOk("javascript:alert(1)")).toBe(false);
    expect(codeLinkUrlOk("https://u:p@git.example.org/")).toBe(false);
    expect(codeLinkUrlOk("https://u@git.example.org/")).toBe(false);
    expect(codeLinkUrlOk("nonsense")).toBe(false);
    expect(codeLinkUrlOk(`https://git.example.org/${"a".repeat(500)}`)).toBe(false);
  });
});
