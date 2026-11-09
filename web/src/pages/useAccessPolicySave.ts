import { useState } from "react";
import { ApiError } from "../api/client";
import { api } from "../api/endpoints";
import { ACCESS_POLICY_CHANGED, type AccessPolicyDocument, type AccessPolicyView } from "../api/typesAccess";
import { useI18n } from "../i18n";
import { approvalKeys } from "../lib/approvalKey";
import { canonicalDocument, validateDocument } from "../lib/accessPolicy";
import { encodePolicy, hashOfEncoding, policyPayload } from "../lib/accessPolicyHash";
import { useCodeGate, type CodeGate } from "./useCodeGate";

export interface AccessPolicySaver {
  /** Canonicalises, checks, hashes and (when this browser holds a key) signs the document, then saves it with the code gate. */
  submit: (document: AccessPolicyDocument) => Promise<void>;
  gate: CodeGate;
  /** A problem found before the save went out (the document, or signing); the API's answers are in gate.error. */
  problem: string | null;
}

/**
 * Saving a project's access policy (ADR-0020 §4): the browser sends the canonical form it signed, for version + 1 of what it edited. The API asks
 * for the code (reauth_required) and refuses a save that came second (access_policy_changed: onConflict reloads what is there now).
 */
export function useAccessPolicySave(view: AccessPolicyView, ws: string, onSaved: (signed: boolean) => void, onConflict: () => void): AccessPolicySaver {
  const { t, tx } = useI18n();
  const gate = useCodeGate("reauth_required", "access.codeHint");
  const [problem, setProblem] = useState<string | null>(null);

  const submit = async (draft: AccessPolicyDocument) => {
    setProblem(null);
    const document = canonicalDocument(draft);
    const encoded = encodePolicy(document);
    const invalid = validateDocument(document, encoded.length);
    if (invalid) return setProblem(tx(`errors.${invalid}`));
    const hash = await hashOfEncoding(encoded);
    let proof: { keyId: string; signature: string } | null;
    try {
      proof = await approvalKeys.sign(policyPayload(ws, view.projectId, view.version + 1, hash));
    } catch {
      // The browser holds a key but could not use it: saving unsigned would be ignored where signatures are required, so nothing is sent.
      return setProblem(t("access.signFailed"));
    }
    let conflict = false;
    const ok = await gate.attempt(async (code) => {
      try {
        await api.saveAccessPolicy(view.projectId, { expectedVersion: view.version, document, keyId: proof?.keyId, signature: proof?.signature, code });
      } catch (e) {
        if (e instanceof ApiError && e.code === ACCESS_POLICY_CHANGED) conflict = true;
        throw e;
      }
    });
    if (conflict) onConflict();
    else if (ok) onSaved(proof !== null);
  };

  return { submit, gate, problem };
}
