import { describe, expect, it } from "vitest";
import type { AccessPolicyDocument } from "../api/typesAccess";
import { SIGNING } from "./approvalKey";
import { encodePolicy, policyHash, policyPayload } from "./accessPolicyHash";

// One vector shared with AccessPolicySigningVectorTests.cs (ADR-0020 §4 rule 3): the same document must give the same bytes, hash and payload in
// the browser and in C#, and the signature (made once by Node's WebCrypto; the private half of this throwaway key was discarded) verifies in both.
const DOCUMENT: AccessPolicyDocument = {
  format: 1,
  projectKeys: ["git:git.example.invalid/team/shop"],
  includeProjectFolder: true,
  linux: { allowedFolders: ["/srv/caf\u00e9"], deniedFolders: ["/srv/caf\u00e9/secret"], applications: [{ path: "/usr/bin/tail", sha256: "ab".repeat(32) }] },
  macOs: { allowedFolders: ["/Users/dev/work"], deniedFolders: [], applications: [{ path: "/usr/bin/make", sha256: null }] },
  windows: { allowedFolders: ["C:\\Work\\Shop"], deniedFolders: [], applications: [] },
  shell: false,
  cleared: false,
};
const ENCODED_LENGTH = 310;
const HASH = "83935011bc72cf1ecd20f94d12bf92a9996a26427dce9a00791a7bfc15d98f79";
const WORKSPACE = "0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e70";
const PROJECT = "0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e71";
const PAYLOAD = `ap-access-policy-v1\n${WORKSPACE}\n${PROJECT}\n3\n${HASH}`;
const KEY = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjAki8RDcqzMRG5/pFxCllxu5rY3N8smmesYh1PciGOvtLmcS1b7FxgGyhGzerDrnYBGknIb08ft4ivfiLNc0nQ=="; // gitleaks:allow
const SIGNATURE = "grm0HT3Xlp3vyvkb5UTLi6VRV3GPYjS3lXZup9VmtxPyqpvmfaAKMI+sgk0h33lfkflnCYQF9sKnwxZfStwUew==";

const bytes = (base64: string) => Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
const verify = async (payload: string) =>
  crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, await crypto.subtle.importKey("spki", bytes(KEY), SIGNING, true, ["verify"]), bytes(SIGNATURE),
    new TextEncoder().encode(payload));

describe("the access policy vector shared with C#", () => {
  it("encodes the document to the same bytes and hash as AccessPolicySigning", async () => {
    expect(encodePolicy(DOCUMENT)).toHaveLength(ENCODED_LENGTH);
    expect(await policyHash(DOCUMENT)).toBe(HASH);
  });

  it("builds the same payload, ids in lower case, and the shared signature verifies over it and over no other version", async () => {
    expect(policyPayload(WORKSPACE.toUpperCase(), PROJECT.toUpperCase(), 3, HASH)).toBe(PAYLOAD);
    expect(await verify(PAYLOAD)).toBe(true);
    expect(await verify(policyPayload(WORKSPACE, PROJECT, 4, HASH))).toBe(false);
    expect(await verify(policyPayload(WORKSPACE, PROJECT, 3, "0".repeat(64)))).toBe(false);
  });

  it("moves the hash for a null against an empty SHA-256 and for each flag", async () => {
    const empty = { ...DOCUMENT, macOs: { ...DOCUMENT.macOs, applications: [{ path: "/usr/bin/make", sha256: "" }] } };
    expect(await policyHash(empty)).not.toBe(HASH);
    expect(await policyHash({ ...DOCUMENT, shell: true })).not.toBe(HASH);
    expect(await policyHash({ ...DOCUMENT, cleared: true })).not.toBe(HASH);
    expect(await policyHash({ ...DOCUMENT, includeProjectFolder: false })).not.toBe(HASH);
  });
});
