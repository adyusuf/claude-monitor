// The canonical form and the limits of an access policy document, mirroring AccessPolicyRules.cs (ADR-0020 §4). The browser sends and
// signs the canonical form; the API refuses a body that is not canonical and judges every limit again (the check here is for the person).
import type { AccessApp, AccessOsRules, AccessPolicyDocument, OsKind } from "../api/typesAccess";
import { OS_KINDS } from "../api/typesAccess";
import { canonicalPath, hasNoHidden, isAbsoluteExecutable, isForbiddenRoot, isValidPath, isWellFormed, nfc } from "./accessPaths";

export const ACCESS_FORMAT = 1;
export const MAX_PROJECT_KEYS = 20;
export const MAX_PROJECT_KEY_LENGTH = 200;
export const MAX_FOLDERS = 32;
export const MAX_APPLICATIONS = 32;
export const MAX_ENCODED_BYTES = 65_536;
const SHA256_HEX = /^[0-9a-f]{64}$/;

/** The reasons a document is refused (AccessPolicyErrors); the same codes the API answers with. */
export const POLICY_ERRORS = {
  malformed: "policy_malformed", format: "policy_format", keys: "policy_keys", tooMany: "policy_too_many", path: "policy_path",
  forbiddenRoot: "policy_forbidden_root", app: "policy_app", sha256: "policy_sha256", cleared: "policy_cleared", tooLarge: "policy_too_large",
} as const;
export type PolicyError = (typeof POLICY_ERRORS)[keyof typeof POLICY_ERRORS];

export const emptyRules = (): AccessOsRules => ({ allowedFolders: [], deniedFolders: [], applications: [] });

/** What a project without a saved policy shows: its own folder on each machine, no other folder, any application, no shell. */
export const defaultDocument = (projectKeys: string[]): AccessPolicyDocument => ({
  format: ACCESS_FORMAT, projectKeys, includeProjectFolder: true, linux: emptyRules(), macOs: emptyRules(), windows: emptyRules(), shell: false, cleared: false,
});

/** A taken-back policy: the same keys, every list empty and no shell, so the default applies again. */
export const clearedDocument = (projectKeys: string[]): AccessPolicyDocument => ({ ...defaultDocument(projectKeys), includeProjectFolder: false, cleared: true });

/** The section of the document for one OS. */
export const sectionOf = (document: AccessPolicyDocument, os: OsKind): AccessOsRules => {
  switch (os) {
    case "linux":
      return document.linux;
    case "macos":
      return document.macOs;
    case "windows":
      return document.windows;
    default:
      return emptyRules();
  }
};

/** The document with one OS's section replaced. */
export const withSection = (document: AccessPolicyDocument, os: OsKind, rules: AccessOsRules): AccessPolicyDocument => {
  switch (os) {
    case "linux":
      return { ...document, linux: rules };
    case "macos":
      return { ...document, macOs: rules };
    case "windows":
      return { ...document, windows: rules };
    default:
      return document;
  }
};

// Ordinal (UTF-16 code unit) order, as StringComparer.Ordinal; null before any text.
const ordinal = (a: string | null, b: string | null) => (a === b ? 0 : a === null ? -1 : b === null ? 1 : a < b ? -1 : 1);
const sorted = (values: string[]) => [...new Set(values)].sort(ordinal);
const asciiLower = (text: string) => text.replace(/[A-Z]/g, (c) => c.toLowerCase());

function canonicalRules(rules: AccessOsRules | undefined, os: OsKind): AccessOsRules {
  if (!rules) return emptyRules();
  const seen = new Set<string>();
  const apps: AccessApp[] = [];
  for (const app of rules.applications ?? []) {
    if (app?.path === undefined || app.path === null) continue;
    const next = { path: canonicalPath(app.path, os), sha256: app.sha256 === null || app.sha256 === undefined ? null : asciiLower(app.sha256) };
    const id = JSON.stringify([next.path, next.sha256]);
    if (seen.has(id)) continue;
    seen.add(id);
    apps.push(next);
  }
  apps.sort((a, b) => ordinal(a.path, b.path) || ordinal(a.sha256, b.sha256));
  const paths = (list: string[] | undefined) => sorted((list ?? []).filter((p): p is string => typeof p === "string").map((p) => canonicalPath(p, os)));
  return { allowedFolders: paths(rules.allowedFolders), deniedFolders: paths(rules.deniedFolders), applications: apps };
}

/** The canonical form (AccessPolicyRules.Canonical): what the browser hashes, signs and sends. */
export const canonicalDocument = (document: AccessPolicyDocument): AccessPolicyDocument => ({
  ...document,
  projectKeys: sorted((document.projectKeys ?? []).filter((k): k is string => typeof k === "string").map(nfc)),
  linux: canonicalRules(document.linux, "linux"),
  macOs: canonicalRules(document.macOs, "macos"),
  windows: canonicalRules(document.windows, "windows"),
});

/** A repository key: 1 to 200 characters, none of them a control, bidirectional or zero-width character. */
export const isValidKey = (key: string) => key.length >= 1 && key.length <= MAX_PROJECT_KEY_LENGTH && isWellFormed(key) && hasNoHidden(key);

/** Lowercase hex, 64 characters. */
export const isSha256 = (text: string) => SHA256_HEX.test(text);

/** Why one folder is refused, or null: not a valid absolute path for the OS, or (for an allowed folder) too close to the root. */
export function folderProblem(path: string, os: OsKind, allowed: boolean): PolicyError | null {
  const canonical = canonicalPath(path, os);
  if (!isValidPath(canonical, os)) return POLICY_ERRORS.path;
  return allowed && isForbiddenRoot(canonical, os) ? POLICY_ERRORS.forbiddenRoot : null;
}

/** Why one application is refused, or null. */
export function appProblem(app: AccessApp, os: OsKind): PolicyError | null {
  const path = canonicalPath(app.path, os);
  if (!isValidPath(path, os) || !isAbsoluteExecutable(path, os)) return POLICY_ERRORS.app;
  return app.sha256 === null || isSha256(asciiLower(app.sha256)) ? null : POLICY_ERRORS.sha256;
}

const isComplete = (rules: AccessOsRules | undefined) =>
  Array.isArray(rules?.allowedFolders) && Array.isArray(rules?.deniedFolders) && Array.isArray(rules?.applications)
  && rules.allowedFolders.every((p) => typeof p === "string") && rules.deniedFolders.every((p) => typeof p === "string")
  && rules.applications.every((a) => typeof a?.path === "string");

function sectionError(rules: AccessOsRules, os: OsKind): PolicyError | null {
  if (rules.allowedFolders.length > MAX_FOLDERS || rules.deniedFolders.length > MAX_FOLDERS || rules.applications.length > MAX_APPLICATIONS) return POLICY_ERRORS.tooMany;
  if (!rules.allowedFolders.every((p) => isValidPath(p, os)) || !rules.deniedFolders.every((p) => isValidPath(p, os))) return POLICY_ERRORS.path;
  if (rules.allowedFolders.some((p) => isForbiddenRoot(p, os))) return POLICY_ERRORS.forbiddenRoot;
  if (!rules.applications.every((a) => isValidPath(a.path, os) && isAbsoluteExecutable(a.path, os))) return POLICY_ERRORS.app;
  return rules.applications.every((a) => a.sha256 === null || isSha256(a.sha256)) ? null : POLICY_ERRORS.sha256;
}

const hasEntries = (rules: AccessOsRules) => rules.allowedFolders.length + rules.deniedFolders.length + rules.applications.length > 0;

/**
 * Null when the document is valid as it is, else the code the API would answer (AccessPolicyRules.Validate, in the same order). encodedLength is
 * the byte length of its hash encoding (accessPolicyHash.encodePolicy), which only the caller that hashes has at hand.
 */
export function validateDocument(document: AccessPolicyDocument, encodedLength?: number): PolicyError | null {
  if (!Array.isArray(document.projectKeys) || !document.projectKeys.every((k) => typeof k === "string")
    || !isComplete(document.linux) || !isComplete(document.macOs) || !isComplete(document.windows)) return POLICY_ERRORS.malformed;
  if (document.format !== ACCESS_FORMAT) return POLICY_ERRORS.format;
  if (document.projectKeys.length < 1 || document.projectKeys.length > MAX_PROJECT_KEYS || !document.projectKeys.every(isValidKey)) return POLICY_ERRORS.keys;
  for (const os of OS_KINDS) {
    const error = sectionError(sectionOf(document, os), os);
    if (error) return error;
  }
  if (document.cleared && (document.shell || OS_KINDS.some((os) => hasEntries(sectionOf(document, os))))) return POLICY_ERRORS.cleared;
  return encodedLength !== undefined && encodedLength > MAX_ENCODED_BYTES ? POLICY_ERRORS.tooLarge : null;
}
