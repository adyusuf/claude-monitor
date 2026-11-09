// What an access policy's hash covers and what the owner's browser key signs for it, byte for byte as CanonicalBytes.cs and
// AccessPolicySigning.cs (ADR-0020 §4 rule 3). The API and the agent recompute both; a different byte here is a refused save.
import type { AccessPolicyDocument } from "../api/typesAccess";

export const POLICY_HASH_TAG = "ap-access-policy-hash-v1";
export const POLICY_TAG = "ap-access-policy-v1";

/** The length-prefixed encoding: every field is "present flag, big-endian byte length, bytes", so null and empty differ. */
class CanonicalWriter {
  private readonly parts: number[] = [];
  private readonly utf8 = new TextEncoder();

  /** A 32-bit integer, big-endian. */
  int(value: number): void {
    this.parts.push((value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff);
  }

  /** A string: 0 for null, else 1, the UTF-8 byte length and the UTF-8 bytes. */
  text(value: string | null): void {
    if (value === null) {
      this.parts.push(0);
      return;
    }
    const bytes = this.utf8.encode(value);
    this.parts.push(1);
    this.int(bytes.length);
    for (const b of bytes) this.parts.push(b);
  }

  /** A list of strings: 0 for null, else 1, the count and each string. */
  list(values: readonly string[] | null): void {
    if (values === null) {
      this.parts.push(0);
      return;
    }
    this.parts.push(1);
    this.int(values.length);
    for (const value of values) this.text(value);
  }

  bytes(): Uint8Array {
    return Uint8Array.from(this.parts);
  }
}

const flag = (value: boolean) => (value ? 1 : 0);

/** The bytes the hash covers (AccessPolicySigning.Encode): tag, format, keys, the project-folder flag, Linux, macOS, Windows, shell, cleared. */
export function encodePolicy(document: AccessPolicyDocument): Uint8Array {
  const w = new CanonicalWriter();
  w.text(POLICY_HASH_TAG);
  w.int(document.format);
  w.list(document.projectKeys);
  w.int(flag(document.includeProjectFolder));
  for (const rules of [document.linux, document.macOs, document.windows]) {
    w.list(rules.allowedFolders);
    w.list(rules.deniedFolders);
    w.int(rules.applications.length);
    for (const app of rules.applications) {
      w.text(app.path);
      w.text(app.sha256);
    }
  }
  w.int(flag(document.shell));
  w.int(flag(document.cleared));
  return w.bytes();
}

const hex = (bytes: ArrayBuffer): string => [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, "0")).join("");

/** Lowercase hex SHA-256 of the encoding (AccessPolicySigning.Hash). */
export const hashOfEncoding = async (encoded: Uint8Array): Promise<string> => hex(await crypto.subtle.digest("SHA-256", encoded as BufferSource));

export const policyHash = (document: AccessPolicyDocument): Promise<string> => hashOfEncoding(encodePolicy(document));

/** The exact text signed (AccessPolicySigning.Payload): the tag, the workspace, the project, the version and the hash, one per line. */
export const policyPayload = (workspaceId: string, projectId: string, version: number, hash: string): string =>
  `${POLICY_TAG}\n${workspaceId.toLowerCase()}\n${projectId.toLowerCase()}\n${version}\n${hash}`;
