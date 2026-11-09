// The access policy of a managed project (ADR-0020 §4) as the API sends it: the camelCase JSON of the records in
// src/Podium.Contracts/AccessProtocol.cs. Only grows, like the contracts.

/** The operating systems a policy has a section for (OsKinds in the contracts). */
export const OS_KINDS = ["linux", "macos", "windows"] as const;
export type OsKind = (typeof OS_KINDS)[number];

/** An allowed executable by its absolute path, with the lowercase hex SHA-256 its file must have when one is given. */
export interface AccessApp { path: string; sha256: string | null }

/** One OS's section: folders a run may use, folders it may never use, executables it may start (an empty list: no limit). */
export interface AccessOsRules { allowedFolders: string[]; deniedFolders: string[]; applications: AccessApp[] }

/** The policy document as it is hashed and signed. Cleared: the policy was taken back and the default applies again. */
export interface AccessPolicyDocument {
  format: number;
  projectKeys: string[];
  includeProjectFolder: boolean;
  linux: AccessOsRules;
  macOs: AccessOsRules;
  windows: AccessOsRules;
  shell: boolean;
  cleared: boolean;
}

/** A project's policy as the project page shows it. isDefault: none was saved yet (version 0, the default document). */
export interface AccessPolicyView {
  projectId: string;
  version: number;
  document: AccessPolicyDocument;
  hash: string;
  isDefault: boolean;
  isSigned: boolean;
  signerName: string | null;
  candidateKeys: string[];
  /** Machines that require signed approvals: they ignore a version nobody signed and keep their last signed one. */
  machinesRequiringSignature: number;
  canEdit: boolean;
}

/** What the web sends to save a policy. keyId and signature sign version expectedVersion + 1; code is the re-authentication code. */
export interface AccessPolicySave {
  expectedVersion: number;
  document: AccessPolicyDocument;
  keyId?: string;
  signature?: string;
  code?: string;
}

/** The policy that covers a repository, for the session page; the API answers 404 when none does. */
export interface AccessPolicySummary { projectId: string; projectName: string; version: number; isSigned: boolean; shell: boolean }

/** The machine's own ceiling, set by its admin with aipod config access. Shown only to the machine's owner and the workspace's admins. */
export interface AccessCeilingReport { allowedFolders: string[]; deniedFolders: string[]; applications: AccessApp[]; shell: boolean }

/** One project's policy on one machine: the web's version, what the machine applied and in which state, and the effective lists for its OS. */
export interface MachineAccessPolicyView {
  projectId: string;
  projectName: string;
  webVersion: number;
  appliedVersion: number | null;
  state: string | null;
  isSigned: boolean;
  allowedFolders: string[];
  deniedFolders: string[];
  applications: AccessApp[];
  includeProjectFolder: boolean;
  shell: boolean;
}

/** A machine's access. reportedAt null: the agent sent no report (too old to apply policies). defaultApplies: a run without a policy gets the default. */
export interface MachineAccessView {
  ceiling: AccessCeilingReport | null;
  policies: MachineAccessPolicyView[];
  reportedAt: string | null;
  defaultApplies: boolean;
}

/** The state of a delivered policy on a machine (AccessPolicyStates). Anything but applied and kept_signed means the default applies. */
export const ACCESS_STATES = ["applied", "kept_signed", "default", "unsigned_ignored", "key_unknown", "invalid", "rollback_refused"] as const;

/** Why a target refused a run under the access layers (AccessErrors), in the run's error. */
export const ACCESS_ERROR_PREFIX = "access_";
export const ACCESS_ERRORS = [
  "access_cwd_not_allowed", "access_cwd_denied", "access_path_not_allowed", "access_path_denied", "access_exe_not_allowed", "access_exe_hash",
  "access_shell_refused", "access_script_refused", "access_unresolved", "access_stale",
] as const;

/** Which layer refused a run (AccessRefusedBy). */
export const ACCESS_REFUSED_BY = ["ceiling", "policy", "default"] as const;

/** The save endpoint's conflict codes: another save came first, or a repository key belongs to another project's policy. */
export const ACCESS_POLICY_CHANGED = "access_policy_changed";
export const ACCESS_KEY_TAKEN = "access_key_taken";
