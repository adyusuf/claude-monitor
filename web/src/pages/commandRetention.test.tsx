import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CommandRow } from "../api/types";
import { de } from "../i18n/de";
import { en } from "../i18n/en";
import { tr } from "../i18n/tr";
import { ME, mockApi, renderAt } from "../test/helpers";
import { CommandItem } from "./CommandHistory";

// F-119: a prompt whose text was removed after the workspace's retention says so, instead of showing an empty line.
const NOW = Date.parse("2026-10-05T12:00:00Z");

const command = (over: Partial<CommandRow> = {}): CommandRow => ({
  id: "c1", kind: "prompt", body: "Run the tests", status: "applied", createdBy: "u1", createdAt: "2026-07-01T10:00:00Z",
  expiresAt: "2026-07-01T10:30:00Z", deliveredAt: "2026-07-01T10:01:00Z", appliedAt: "2026-07-01T10:02:00Z", result: null, ...over,
});

const show = (c: CommandRow) => {
  mockApi({ "GET /me": { body: ME } });
  renderAt("/x", [{ path: "/x", element: <ul><CommandItem command={c} sessionId="s1" ended={false} now={NOW} canCancel={false} onCancel={() => undefined} /></ul> }]);
};

afterEach(() => vi.restoreAllMocks());

describe("a prompt past retention", () => {
  it("says its text was removed after retention", async () => {
    show(command({ body: null, contentRemoved: true }));
    expect(await screen.findByText(en.session.removedAfterRetention)).toBeInTheDocument();
  });

  it("shows the text of a prompt that was not removed, and nothing about retention (also from an older API without the field)", async () => {
    show(command());
    expect(await screen.findByText("Run the tests")).toBeInTheDocument();
    expect(screen.queryByText(en.session.removedAfterRetention)).toBeNull();
  });

  it("has the words in every language", () => {
    for (const dict of [en, tr, de]) expect(dict.session.removedAfterRetention.length).toBeGreaterThan(0);
    expect(new Set([en, tr, de].map((d) => d.session.removedAfterRetention)).size).toBe(3);
  });
});
