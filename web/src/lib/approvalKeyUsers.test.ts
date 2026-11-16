import { afterEach, describe, expect, it, vi } from "vitest";
import { createApprovalKeys, LEGACY_SLOT, memoryStores, userScopedStore, type StoredKey } from "./approvalKey";

/** One browser shared by two accounts (F-190): each person's key is their own, and an old browser-wide key goes only to the person it belongs to. */
const browser = () => {
  const slots = memoryStores();
  const scope = userScopedStore(slots);
  return { slots, scope, keys: createApprovalKeys(scope) };
};
const legacyKey = (fingerprint = "a".repeat(64)): StoredKey => ({ name: "old", fingerprint, publicKey: "cHVibGlj", privateKey: { type: "private" } as unknown as CryptoKey });

afterEach(() => vi.restoreAllMocks());

describe("the approval key of each person on one browser", () => {
  it("keeps a key per person: the next account neither reads nor signs with the previous one's, and gets its own back later", async () => {
    const { scope, keys } = browser();
    scope.useFor("u1");
    const mine = await keys.generate("laptop");

    scope.useFor("u2");
    expect(await keys.current()).toBeNull();
    expect(await keys.sign("payload")).toBeNull();
    const theirs = await keys.generate("desk");

    scope.useFor("u1");
    expect((await keys.current())?.fingerprint).toBe(mine.fingerprint);
    expect((await keys.sign("payload"))?.keyId).toBe(mine.fingerprint);
    expect(theirs.fingerprint).not.toBe(mine.fingerprint);
  });

  it("holds nothing while nobody is signed in, and forgetting one person's key leaves the other's", async () => {
    const { scope, keys } = browser();
    scope.useFor("u1");
    await keys.generate("laptop");
    scope.useFor("u2");
    await keys.generate("desk");
    await keys.forget();

    scope.useFor(null);
    expect(await keys.current()).toBeNull();
    scope.useFor("u1");
    expect((await keys.current())?.name).toBe("laptop");
  });

  it("hands the old browser-wide key to the person whose live key it is, and empties the old slot", async () => {
    const { slots, scope, keys } = browser();
    const old = legacyKey();
    await slots(LEGACY_SLOT).put(old);
    scope.useFor("u1");
    const isLive = vi.fn(async (fingerprint: string) => fingerprint === old.fingerprint);

    expect(await scope.adoptLegacy(isLive)).toBe(true);

    expect(await keys.current()).toBe(old);
    expect(await slots(LEGACY_SLOT).get()).toBeNull();
    expect(isLive).toHaveBeenCalledWith(old.fingerprint);
  });

  it("leaves the old key where it is for a person it does not belong to: they get none and cannot sign with it", async () => {
    const { slots, scope, keys } = browser();
    const old = legacyKey();
    await slots(LEGACY_SLOT).put(old);
    scope.useFor("u2");

    expect(await scope.adoptLegacy(async () => false)).toBe(false);

    expect(await keys.current()).toBeNull();
    expect(await keys.sign("payload")).toBeNull();
    expect(await slots(LEGACY_SLOT).get()).toBe(old);
  });

  it("does not ask about the old key when nobody is signed in or the person already has a key of their own", async () => {
    const { slots, scope, keys } = browser();
    await slots(LEGACY_SLOT).put(legacyKey());
    const isLive = vi.fn(async () => true);

    expect(await scope.adoptLegacy(isLive)).toBe(false);
    scope.useFor("u1");
    const own = await keys.generate("laptop");
    expect(await scope.adoptLegacy(isLive)).toBe(false);

    expect(isLive).not.toHaveBeenCalled();
    expect((await keys.current())?.fingerprint).toBe(own.fingerprint);
  });
});
