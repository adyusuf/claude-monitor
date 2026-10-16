import { screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CommandRow } from "../api/types";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { CommandItem } from "./CommandHistory";

const NOW = Date.parse("2026-10-05T12:00:00Z");
const minutesAgo = (m: number) => new Date(NOW - m * 60_000).toISOString();

const command = (over: Partial<CommandRow> = {}): CommandRow => ({
  id: "c1", kind: "prompt", body: "Run the tests", status: "applied", createdBy: "u1", createdAt: minutesAgo(5),
  expiresAt: minutesAgo(-25), deliveredAt: minutesAgo(4), appliedAt: minutesAgo(1), result: null, ...over,
});

const show = (c: CommandRow) => {
  vi.spyOn(Date, "now").mockReturnValue(NOW);
  mockApi({ "GET /me": { body: ME } });
  renderAt("/x", [{ path: "/x", element: <ul><CommandItem command={c} sessionId="s1" ended={false} now={NOW} canCancel onCancel={() => undefined} /></ul> }]);
  return screen.getByText("Run the tests").closest("li")!;
};

afterEach(() => vi.restoreAllMocks());

describe("the way an applied command reached its session", () => {
  it.each([
    ["hook", en.commandTransport.hook],
    ["inbox", en.commandTransport.inbox],
    ["channel", en.commandTransport.channel],
  ])("says so for %s", (transport, how) => {
    const li = show(command({ transport }));
    expect(within(li).getByText(en.commandTransport.via.replace("{how}", how))).toBeInTheDocument();
  });

  it("says nothing for an older API or agent, a command not applied yet, or a way it does not know", () => {
    for (const c of [command(), command({ transport: null }), command({ status: "delivered", transport: "hook" }), command({ transport: "carrier-pigeon" })]) {
      const li = show(c);
      expect(within(li).queryByText(/Reached the session/)).toBeNull();
      document.body.innerHTML = "";
    }
  });
});

describe("a command pushed into the session that it has not shown yet", () => {
  it.each([
    ["inbox", en.commandTransport.inbox],
    ["channel", en.commandTransport.channel],
  ])("says it was pushed %s and waits for the session, not for the next hook", (transport, how) => {
    const li = show(command({ status: "delivered", transport, appliedAt: null }));
    expect(within(li).getByText(en.commandHint.pushed.replace("{how}", how), { exact: false })).toBeInTheDocument(); // the countdown follows in the same line
    expect(within(li).queryByText(en.commandHint.delivered, { exact: false })).toBeNull();
  });

  it("keeps the older hint when nothing was pushed", () => {
    const li = show(command({ status: "delivered", transport: null, appliedAt: null }));
    expect(within(li).getByText(en.commandHint.delivered, { exact: false })).toBeInTheDocument();
  });
});
