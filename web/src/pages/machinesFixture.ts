import { act, waitFor } from "@testing-library/react";
import { expect } from "vitest";
import type { AgentRow } from "../api/types";
import type { Call } from "../test/helpers";

export const agent = (over: Partial<AgentRow> = {}): AgentRow => ({
  id: "a1", machineId: "m1", hostname: "laptop-1", os: "linux", arch: "x64", version: "0.4.0", status: "active", userId: "u2", userName: "Diğer Kişi",
  enrolledAt: new Date(2026, 9, 1).toISOString(), lastHeartbeatAt: new Date(2026, 9, 5, 9, 30).toISOString(), revokedAt: null, ...over,
});

export const text = (c: Element) => (c.textContent ?? "").replace(/\s+/g, " ").trim();
export const count = (calls: Call[], path: string) => calls.filter((c) => c.path.split("?")[0] === path).length;

/** The page reads once before the signed-in user is known and once after; wait for both so a count is a baseline. */
export const settle = async (calls: Call[]) => {
  await waitFor(() => expect(calls.filter((c) => c.path.startsWith("/workspaces/w1/runs") && !c.path.includes("before=")).length).toBeGreaterThanOrEqual(2));
  await act(async () => {});
};
