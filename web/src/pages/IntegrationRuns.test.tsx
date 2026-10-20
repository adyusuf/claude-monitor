import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { dictionaries, I18nProvider, type Language } from "../i18n";
import { localIso, syncRun } from "../test/integrations-fixtures";
import { IntegrationRuns } from "./IntegrationRuns";
import type { SyncRunView } from "../api/typesIntegrations";

const show = (runs: SyncRunView[], language: Language = "en") => render(<I18nProvider initial={language}><IntegrationRuns runs={runs} /></I18nProvider>);

describe("the recent checks of a connection", () => {
  it("says nothing has run yet, with no list", () => {
    show([]);
    expect(screen.getByText("Nothing has run yet.")).toBeInTheDocument();
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it.each([
    ["ok", "Done", "badge ok"],
    ["running", "Running", "badge"],
    ["partial", "Partly done", "badge warn"],
    ["failed", "Failed", "badge warn"],
  ] as const)("gives a %s check the %s badge in the colour %s", (status, text, classes) => {
    show([syncRun({ status })]);
    const badge = screen.getByText(text);
    expect(badge.className).toBe(classes);
  });

  it("shows the kind, the start as dd/mm/yyyy hh:mm and the counts", () => {
    show([syncRun({ kind: "webhook", startedAt: localIso(2026, 10, 3, 7, 5), created: 3, updated: 4, archived: 1, people: 2 })]);
    const row = screen.getByRole("listitem");
    expect(row).toHaveTextContent("Update from Jira · 03/10/2026 07:05 · 3 new · 4 updated · 1 archived · 2 people");
  });

  it("shows one row per check in the order given", () => {
    show([syncRun({ id: "r1", kind: "import" }), syncRun({ id: "r2", kind: "pass" })]);
    const rows = screen.getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("Import");
    expect(rows[1]).toHaveTextContent("Check");
  });

  it("shows the error in words, from its code, and never anything else from the tool", () => {
    show([syncRun({ status: "failed", error: "tracker_rate_limited" })]);
    expect(screen.getByText("Jira asks to slow down. Try again in a minute.")).toHaveClass("muted");
  });

  it("shows no error part for a check that did not fail", () => {
    show([syncRun({ error: null })]);
    expect(screen.getByRole("listitem").querySelector(".muted")).toBeNull();
  });

  it.each(["tr", "de"] as const)("writes the status, the kind and the counts in %s when the page is in that language", (language) => {
    const dict = dictionaries[language].integrations;
    show([syncRun({ status: "partial", kind: "import" })], language);
    expect(screen.getByText(dict.runStatus.partial)).toBeInTheDocument();
    expect(screen.getByRole("listitem")).toHaveTextContent(dict.runKind.import);
    expect(screen.getByRole("listitem")).toHaveTextContent(dict.runCounts.replace("{created}", "3").replace("{updated}", "4").replace("{archived}", "1").replace("{people}", "2"));
    expect(screen.queryByText("Partly done")).not.toBeInTheDocument();
  });
});
