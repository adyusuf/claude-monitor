import { describe, expect, it } from "vitest";
import { checkApplication, draftOfApplication, emptyApplicationDraft, inputOfApplication } from "./applicationForm";
import { day } from "./format";
import { checkProject, draftOfProject, emptyProjectDraft, filled, inputOfProject } from "./projectForm";
import { canWorkInProjects, isWebLink } from "./projectOptions";
import { application, project } from "../test/projects-fixtures";

describe("the project form", () => {
  it("starts from sensible defaults and round-trips a stored project", () => {
    const draft = emptyProjectDraft("u1");
    expect(inputOfProject({ ...draft, name: " X " })).toMatchObject({ name: "X", ownerUserId: "u1", sprintLengthDays: 14, priorityScale: "p0_p3", estimationUnit: "story_points" });
    const stored = project();
    const sent = inputOfProject(draftOfProject(stored));
    expect(sent).toMatchObject({ name: stored.name, budgetAmount: 1500, budgetCurrency: "EUR", itemTypes: ["story", "bug"], definitionOfDone: ["Reviewed", "Tested"] });
    expect(draftOfProject(project({ purpose: null, sponsor: null, budgetAmount: null, budgetCurrency: null, startDate: null, endDate: null,
      stakeholders: [{ name: null, role: null }] })).stakeholders).toEqual([{ name: "", role: "" }]);
  });

  it("drops blank lines and stakeholder rows, and trims what is kept", () => {
    expect(filled([" a ", "", "  "])).toEqual(["a"]);
    const sent = inputOfProject({ ...emptyProjectDraft("u1"), name: "n", stakeholders: [{ name: " Dana ", role: " " }, { name: "", role: "" }], definitionOfDone: ["", " x "] });
    expect(sent.stakeholders).toEqual([{ name: "Dana", role: null }]);
    expect(sent.definitionOfDone).toEqual(["x"]);
    expect(inputOfProject({ ...emptyProjectDraft(""), name: "n" }).ownerUserId).toBeNull();
  });

  it("accepts a valid form and names every field the API would refuse", () => {
    const ok = { ...emptyProjectDraft("u1"), name: "n" };
    expect(checkProject(ok)).toEqual({});
    const bad = checkProject({
      ...ok, name: "x".repeat(101), purpose: "p".repeat(2001), sponsor: "s".repeat(201), stakeholders: [{ name: "", role: "r" }], budgetAmount: "5", budgetCurrency: "",
      startDate: "2026-02-02", endDate: "2026-02-01", itemTypes: [], sprintLengthDays: "43", definitionOfReady: ["x".repeat(301)],
      definitionOfDone: Array.from({ length: 31 }, () => "x"),
    });
    expect(bad).toEqual({
      name: "invalid_name", purpose: "too_long", sponsor: "too_long", stakeholders: "invalid_list", budget: "amount_and_currency", endDate: "before_start",
      itemTypes: "invalid_list", sprintLengthDays: "out_of_range", definitionOfReady: "invalid_list", definitionOfDone: "invalid_list",
    });
    expect(checkProject({ ...ok, budgetAmount: "-1", budgetCurrency: "EUR" }).budgetAmount).toBe("out_of_range");
    expect(checkProject({ ...ok, budgetAmount: "1", budgetCurrency: "eur" }).budgetCurrency).toBe("invalid_currency");
    expect(checkProject({ ...ok, budgetAmount: "", budgetCurrency: "EUR" }).budget).toBe("amount_and_currency");
    expect(checkProject({ ...ok, sprintLengthDays: "1.5" }).sprintLengthDays).toBe("out_of_range");
    expect(checkProject({ ...ok, stakeholders: Array.from({ length: 51 }, () => ({ name: "a", role: "" })) }).stakeholders).toBe("invalid_list");
  });
});

describe("the application form", () => {
  it("round-trips a stored application and keeps only well-formed rows", () => {
    expect(inputOfApplication({ ...emptyApplicationDraft(), name: " A " })).toMatchObject({ name: "A", kind: "web", versioningScheme: "semver", techOwnerUserId: null });
    const draft = draftOfApplication(application({ environments: [{ name: null, url: null }, { name: "test", url: null }], nonFunctionalTargets: [{ kind: null, target: null }, { kind: "sla", target: null }],
      description: null, repoUrl: null, techOwnerUserId: null }));
    expect(draft.environments).toEqual([{ name: "test", url: "" }]);
    expect(draft.nonFunctionalTargets).toEqual([{ kind: "sla", target: "" }]);
  });

  it("checks the repository and environment addresses like the API does", () => {
    const ok = { ...emptyApplicationDraft(), name: "a" };
    expect(checkApplication(ok)).toEqual({});
    for (const url of ["https://git.example.org/x", "ssh://git@git.example.org/x", "git://git.example.org/x", "git@git.example.org:team/x.git"]) {
      expect(checkApplication({ ...ok, repoUrl: url })).toEqual({});
    }
    for (const url of ["ftp://git.example.org/x", "https://u:p@git.example.org/x", "not a url", `https://git.example.org/${"x".repeat(500)}`]) {
      expect(checkApplication({ ...ok, repoUrl: url }).repoUrl).toBe("invalid_url");
    }
    expect(checkApplication({ ...ok, environments: [{ name: "dev", url: "https://dev.example.org" }, { name: "test", url: "" }] })).toEqual({});
    for (const url of ["javascript:alert(1)", "ftp://dev.example.org", "https://u@dev.example.org", "nonsense"]) {
      expect(checkApplication({ ...ok, environments: [{ name: "dev", url }] }).environments).toBe("invalid_url");
    }
  });

  it("names the other fields the API would refuse", () => {
    const ok = { ...emptyApplicationDraft(), name: "a" };
    expect(checkApplication({ ...ok, name: " ", description: "d".repeat(2001), techStack: Array.from({ length: 31 }, (_, i) => `t${i}`),
      nonFunctionalTargets: [{ kind: "sla", target: " " }] })).toEqual({
      name: "invalid_name", description: "too_long", techStack: "invalid_list", nonFunctionalTargets: "invalid_list",
    });
    expect(checkApplication({ ...ok, nonFunctionalTargets: Array.from({ length: 21 }, () => ({ kind: "sla" as const, target: "x" })) }).nonFunctionalTargets).toBe("invalid_list");
  });
});

describe("project helpers", () => {
  it("shows a calendar day as dd/mm/yyyy without a time zone shift, and other text as it is", () => {
    expect(day("2026-03-09")).toBe("09/03/2026");
    expect(day(null)).toBe("");
    expect(day("soon")).toBe("soon");
  });

  it("links only web addresses and lets members and above work in projects", () => {
    expect(isWebLink("https://x.example.org")).toBe(true);
    expect(isWebLink(" HTTP://x.example.org")).toBe(true);
    for (const text of ["javascript:alert(1)", "data:text/html,x", "git@host:x", "ftp://x"]) expect(isWebLink(text)).toBe(false);
    expect([undefined, "viewer"].map(canWorkInProjects)).toEqual([false, false]);
    expect(["member", "admin", "owner"].map(canWorkInProjects)).toEqual([true, true, true]);
  });
});
