import type { AccessPolicyDocument, AccessPolicyView } from "../api/typesAccess";

export const REPO = "git.example.org/shop";
export const SHA = "a".repeat(64);

const noRules = () => ({ allowedFolders: [], deniedFolders: [], applications: [] });

/** A policy document in canonical form: the project's own folder allowed, nothing listed, no shell. */
export const policyDocument = (over: Partial<AccessPolicyDocument> = {}): AccessPolicyDocument => ({
  format: 1, projectKeys: [REPO], includeProjectFolder: true, linux: noRules(), macOs: noRules(), windows: noRules(), shell: false, cleared: false, ...over,
});

/** A saved, signed policy of version 3 the signed-in user may edit. */
export const policyView = (over: Partial<AccessPolicyView> = {}): AccessPolicyView => ({
  projectId: "p1", version: 3, document: policyDocument(), hash: "ab".repeat(32), isDefault: false, isSigned: true, signerName: "Örnek Kişi",
  candidateKeys: [REPO], machinesRequiringSignature: 0, canEdit: true, ...over,
});

/** What a project without a saved policy reads as: version 0 and the default document. */
export const defaultView = (over: Partial<AccessPolicyView> = {}): AccessPolicyView =>
  policyView({ version: 0, isDefault: true, isSigned: false, signerName: null, ...over });
