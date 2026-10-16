import { describe, expect, it } from "vitest";
import { approvalKeys as shared, createApprovalKeys, fingerprintOf, memoryStore, payloadFor, SIGNING } from "./approvalKey";

// Made by Node's WebCrypto for the C# test of the same name: the agent's verifier checks this very signature.
// The public half of a throwaway test key (its private half was discarded): a test vector, not a secret.
const BROWSER_KEY = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjVzUVruijJwX3hreZFoDwzCsbjf0RYXr+k4vTRcDjPUj5UHd5D5KpBV7fnHqAulEYmymSVtNi037+o52d+s1ng=="; // gitleaks:allow
const BROWSER_SIGNATURE = "QL8DauuF1hk1MwECldHBnuZb8vFDs5fNwLU7f8bd492Je//c0eQPfdEflZ5SAGSX4o2hfV9C2plJnFU5291/dA==";
const BROWSER_PAYLOAD = "cm-approval-v1\nrun\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e60\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e61\n" + "ab".repeat(32) + "\n1792000000";

const bytes = (base64: string) => Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
const importPublic = (base64: string) => crypto.subtle.importKey("spki", bytes(base64), SIGNING, true, ["verify"]);
const verify = async (publicKey: string, signature: string, payload: string) =>
  crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, await importPublic(publicKey), bytes(signature), new TextEncoder().encode(payload));

describe("the payload that is signed", () => {
  it("is the tag, the kind, the run, the target agent, the hash and the expiry in whole seconds, one per line, ids in lower case", () => {
    const iso = new Date(1_792_000_000_000).toISOString();
    expect(payloadFor("run", "0198F1C2-7A55-7D3A-9B1E-2F6C3A4D5E60", "0198F1C2-7A55-7D3A-9B1E-2F6C3A4D5E61", "ab".repeat(32), iso)).toBe(BROWSER_PAYLOAD);
  });

  it("drops fractions of a second so the browser and the server agree on the same number", () => {
    const a = payloadFor("run", "i", "t", "h", "2026-10-16T12:00:00.999Z");
    const b = payloadFor("run", "i", "t", "h", "2026-10-16T12:00:00.000Z");
    expect(a).toBe(b);
  });
});

describe("the key of this browser", () => {
  it("makes a P-256 key, keeps its private half non-extractable and gives the public half as base64 with a fingerprint that is the SHA-256 of it", async () => {
    const keys = createApprovalKeys(memoryStore());
    expect(await keys.current()).toBeNull();
    const key = await keys.generate("laptop");
    expect(key.privateKey.extractable).toBe(false);
    expect(key.privateKey.algorithm).toMatchObject({ name: "ECDSA", namedCurve: "P-256" });
    expect(key.fingerprint).toMatch(/^[0-9a-f]{64}$/);
    expect(key.fingerprint).toBe(await fingerprintOf(key.publicKey));
    expect((await keys.current())?.name).toBe("laptop");
    await expect(crypto.subtle.exportKey("pkcs8", key.privateKey)).rejects.toBeDefined(); // the private half cannot be read out, not even by this code
  });

  it("signs a payload with a 64-byte signature that verifies under its public key and under nothing else", async () => {
    const keys = createApprovalKeys(memoryStore());
    const key = await keys.generate("laptop");
    const proof = (await keys.sign(BROWSER_PAYLOAD))!;
    expect(proof.keyId).toBe(key.fingerprint);
    expect(bytes(proof.signature)).toHaveLength(64);
    expect(await verify(key.publicKey, proof.signature, BROWSER_PAYLOAD)).toBe(true);
    expect(await verify(key.publicKey, proof.signature, BROWSER_PAYLOAD + "x")).toBe(false);
    const other = await createApprovalKeys(memoryStore()).generate("other");
    expect(await verify(other.publicKey, proof.signature, BROWSER_PAYLOAD)).toBe(false);
  });

  it("signs nothing without a key and forgets the key when asked", async () => {
    const keys = createApprovalKeys(memoryStore());
    expect(await keys.sign("x")).toBeNull();
    await keys.generate("a");
    await keys.forget();
    expect(await keys.current()).toBeNull();
    expect(await keys.sign("x")).toBeNull();
  });

  it("makes a different key every time", async () => {
    const keys = createApprovalKeys(memoryStore());
    const one = await keys.generate("a");
    const two = await keys.generate("a");
    expect(one.fingerprint).not.toBe(two.fingerprint);
  });

  it("falls back to a key for this page when the browser has no IndexedDB (the shared instance works here)", async () => {
    await shared.forget();
    const made = await shared.generate("page");
    expect((await shared.current())?.fingerprint).toBe(made.fingerprint);
    await shared.forget();
  });
});

describe("a signature the agent's C# code is checked against", () => {
  it("is a valid signature of the payload for its key, in the 64-byte form WebCrypto makes", async () => {
    expect(bytes(BROWSER_SIGNATURE)).toHaveLength(64);
    expect(await verify(BROWSER_KEY, BROWSER_SIGNATURE, BROWSER_PAYLOAD)).toBe(true);
    expect(await verify(BROWSER_KEY, BROWSER_SIGNATURE, BROWSER_PAYLOAD.replace("1792000000", "1792000001"))).toBe(false);
  });
});
