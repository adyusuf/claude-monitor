import { vi } from "vitest";
import type { ConnectionView, IntegrationsView, PersonView, SpaceView, SprintChoice, SyncRunView } from "../api/typesIntegrations";
import { ME, mockApi } from "./helpers";
import { workspaceMembers } from "./projects-fixtures";

type Routes = Parameters<typeof mockApi>[0];

/** A moment built from local parts, so the page's local-time text ("03/10/2026 07:05") does not depend on the machine's zone. */
export const localIso = (y: number, m: number, d: number, h = 0, min = 0) => new Date(y, m - 1, d, h, min).toISOString();

export const connection = (over: Partial<ConnectionView> = {}): ConnectionView => ({
  id: "c1", provider: "jira", siteHost: "acme.atlassian.net", status: "active", autoInvite: false, connectorName: "Örnek Kişi", connectedByMe: true,
  myAccountLinked: true, myAccountStatus: "active", webhookActive: true, webhookExpiresAt: localIso(2026, 11, 20, 8, 30), lastPassAt: localIso(2026, 10, 3, 7, 5),
  lastError: null, projectCount: 2, ...over,
});

/** Options for finding the card heading of one connection: its name starts with exactly this host (the status badge follows it), never merely contains it. */
export const siteHeading = (host: string) => ({ level: 2, name: (name: string) => name.split(" ")[0] === host });

export const integrationsView = (connections: ConnectionView[] = [connection()], over: Partial<IntegrationsView> = {}): IntegrationsView => ({
  available: true, providers: ["jira"], connections, ...over,
});

export const space = (over: Partial<SpaceView> = {}): SpaceView => ({
  key: "SHOP", name: "Shop", description: null, imported: false, projectId: null, ...over,
});

export const sprintChoice = (over: Partial<SprintChoice> = {}): SprintChoice => ({
  id: "10", name: "Sprint 10", state: "active", goal: null, start: localIso(2026, 3, 2, 10), end: localIso(2026, 3, 12, 10), imported: false, sprintId: null, ...over,
});

export const person = (over: Partial<PersonView> = {}): PersonView => ({
  id: "pe1", displayName: "Mia Jira", email: "mia+test@gmail.com", userId: null, userName: null, invited: false, active: true, ...over,
});

export const syncRun = (over: Partial<SyncRunView> = {}): SyncRunView => ({
  id: "r1", kind: "pass", status: "ok", created: 3, updated: 4, archived: 1, people: 2, error: null, startedAt: localIso(2026, 10, 3, 7, 5), endedAt: localIso(2026, 10, 3, 7, 6), ...over,
});

/** The calls the Integrations page makes for one connection (c1); a test overrides what it cares about. */
export const serveIntegrations = (over: Routes = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /workspaces/w1/integrations": { body: integrationsView() },
  "GET /integrations/c1/runs": { body: [] },
  "GET /integrations/c1/people": { body: [] },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  ...over,
});

/** Replaces the browser's location for a test, so a navigation is recorded instead of attempted; the test file undoes it with vi.unstubAllGlobals(). */
export function stubLocation() {
  const assign = vi.fn();
  vi.stubGlobal("location", { ...window.location, assign });
  return assign;
}
