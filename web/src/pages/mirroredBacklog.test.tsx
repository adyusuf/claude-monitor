import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";
import type { IssueSummary } from "../api/typesIssues";
import { I18nProvider } from "../i18n";
import { issueSummary } from "../test/issues-fixtures";
import { BacklogRow } from "./BacklogRow";

const mirror = { externalKey: "SHOP-12", externalUrl: "https://acme.atlassian.net/browse/SHOP-12" };

/** One backlog row, with spies for the three things it can ask for. */
function row(issue: IssueSummary, canWrite = true) {
  const actions = { onMove: vi.fn(), onArchive: vi.fn(), onRestore: vi.fn() };
  render(
    <I18nProvider initial="en">
      <MemoryRouter><ul><BacklogRow ws="w1" issue={issue} unit="hours" canWrite={canWrite} canMove={canWrite} first={false} last={false} {...actions} /></ul></MemoryRouter>
    </I18nProvider>,
  );
  return actions;
}

describe("a backlog row of a mirrored issue", () => {
  it("shows the key as a link to the tool and no Archive button", () => {
    row(issueSummary(mirror));
    expect(screen.getByRole("link", { name: "SHOP-12 ↗" })).toHaveAttribute("href", mirror.externalUrl);
    expect(screen.queryByRole("button", { name: /Archive/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Restore/ })).not.toBeInTheDocument();
  });

  it("shows no Restore button for an archived mirrored issue either", () => {
    row(issueSummary({ ...mirror, archived: true }));
    expect(screen.getByText("SHOP-12 ↗")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Restore/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Archive/ })).not.toBeInTheDocument();
  });

  it("still lets the person move it", () => {
    row(issueSummary(mirror));
    expect(screen.getByRole("button", { name: "Move up #1" })).toBeEnabled();
  });

  it("shows the key as text when the address is not https", () => {
    row(issueSummary({ externalKey: "SHOP-12", externalUrl: "javascript:alert(1)" }));
    expect(screen.getByText("SHOP-12")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /SHOP-12/ })).not.toBeInTheDocument();
  });
});

describe("a backlog row of an issue made in Podium", () => {
  it("has no badge and keeps the Archive button, which asks to archive", async () => {
    const actions = row(issueSummary());
    expect(screen.queryByText(/SHOP-12/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Archive #1" }));
    expect(actions.onArchive).toHaveBeenCalledTimes(1);
  });

  it("keeps the Restore button when archived, which asks to restore", async () => {
    const actions = row(issueSummary({ archived: true }));
    expect(screen.queryByRole("button", { name: /Archive/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Restore #1" }));
    expect(actions.onRestore).toHaveBeenCalledTimes(1);
  });

  it("shows neither button to a person who may not write", () => {
    row(issueSummary(), false);
    expect(screen.queryByRole("button", { name: /Archive|Restore/ })).not.toBeInTheDocument();
  });
});
