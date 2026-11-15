import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { SubagentRow } from "../api/types";
import { I18nProvider } from "../i18n";
import { SubagentsCard } from "./SubagentsCard";

const run = (over: Partial<SubagentRow> = {}): SubagentRow => ({
  id: "r1", agentType: "analyst", description: null, status: "finished", startedAt: "2026-10-05T10:00:00Z", endedAt: "2026-10-05T10:01:00Z", ...over,
});

const show = (rows: SubagentRow[]) => render(<I18nProvider initial="en"><SubagentsCard rows={rows} /></I18nProvider>);

describe("the status mark of a subagent", () => {
  it("is the check icon for a finished one, not a text symbol (ADR-0018 section 6)", () => {
    const { container } = show([run()]);
    const item = container.querySelector("li.subagent") as HTMLElement;
    expect(item.textContent).not.toContain("✓");
    expect(item.querySelector("svg.icon")).not.toBeNull();
  });

  it("has no check for one that is still running", () => {
    const { container } = show([run({ status: "running", endedAt: null })]);
    const item = container.querySelector("li.subagent") as HTMLElement;
    expect(item.querySelector("svg.icon")).toBeNull();
  });
});
