// The API's shapes for the work tracker integrations (ADR-0015), as IntegrationViews.cs and ImportViews.cs send them. Type-only.

export type ConnectionStatus = "active" | "reauthorize" | "disconnected";

export interface ConnectionView {
  id: string;
  provider: string;
  siteHost: string;
  status: ConnectionStatus;
  autoInvite: boolean;
  connectorName: string;
  connectedByMe: boolean;
  myAccountLinked: boolean;
  myAccountStatus: "active" | "reauthorize" | null;
  webhookActive: boolean;
  webhookExpiresAt: string | null;
  lastPassAt: string | null;
  lastError: string | null;
  projectCount: number;
}

export interface IntegrationsView {
  available: boolean;
  providers: string[];
  connections: ConnectionView[];
}

export interface SpaceView {
  key: string;
  name: string;
  description: string | null;
  imported: boolean;
  projectId: string | null;
}

export type SprintState = "planned" | "active" | "closed";

export interface SprintChoice {
  id: string;
  name: string;
  state: SprintState;
  goal: string | null;
  start: string | null;
  end: string | null;
  imported: boolean;
  sprintId: string | null;
}

export interface SpaceImportResult {
  key: string;
  outcome: string;
  created: number;
  updated: number;
}

export interface SprintImportResult {
  sprintId: string;
  outcome: string;
  created: number;
  updated: number;
}

export interface PersonView {
  id: string;
  displayName: string;
  email: string | null;
  userId: string | null;
  userName: string | null;
  invited: boolean;
  active: boolean;
}

export interface SyncRunView {
  id: string;
  kind: "import" | "webhook" | "pass";
  status: "running" | "ok" | "partial" | "failed";
  created: number;
  updated: number;
  archived: number;
  people: number;
  error: string | null;
  startedAt: string;
  endedAt: string | null;
}
