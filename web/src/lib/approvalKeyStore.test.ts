import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createApprovalKeys, indexedDbStore, type StoredKey } from "./approvalKey";

/**
 * A small stand-in for the browser's IndexedDB that keeps what is put in it by reference (as a structured clone keeps a CryptoKey) and answers
 * the way the real one does: the open request fires `upgradeneeded` for a database it has not seen, then `success`; every request answers on a later turn.
 */
function fakeIndexedDb(fail: { open?: boolean; request?: boolean } = {}) {
  const databases = new Map<string, Map<string, Map<string, unknown>>>();
  const log = { opened: [] as { name: string; version: number | undefined }[], closed: 0, modes: [] as string[] };
  type Req<T> = { result: T; error: unknown; onsuccess: (() => void) | null; onerror: (() => void) | null; onupgradeneeded?: (() => void) | null };
  const request = <T,>(work: () => T): Req<T> => {
    const r: Req<T> = { result: undefined as T, error: null, onsuccess: null, onerror: null };
    queueMicrotask(() => {
      if (fail.request) {
        r.error = new Error("request failed");
        r.onerror?.();
      } else {
        r.result = work();
        r.onsuccess?.();
      }
    });
    return r;
  };
  const factory = {
    open(name: string, version?: number) {
      log.opened.push({ name, version });
      const stores = databases.get(name) ?? new Map<string, Map<string, unknown>>();
      const isNew = !databases.has(name);
      const db = {
        createObjectStore: (store: string) => { stores.set(store, new Map()); },
        close: () => { log.closed += 1; },
        transaction: (store: string, mode: string) => {
          log.modes.push(mode);
          const data = stores.get(store)!;
          return { objectStore: () => ({
            get: (key: string) => request(() => data.get(key)),
            put: (value: unknown, key: string) => request(() => { data.set(key, value); return key; }),
            delete: (key: string) => request(() => { data.delete(key); return undefined; }),
          }) };
        },
      };
      const open: Req<typeof db> = { result: db, error: null, onsuccess: null, onerror: null, onupgradeneeded: null };
      queueMicrotask(() => {
        if (fail.open) {
          open.error = new Error("open failed");
          open.onerror?.();
          return;
        }
        if (isNew) {
          databases.set(name, stores);
          open.onupgradeneeded?.();
        }
        open.onsuccess?.();
      });
      return open;
    },
  };
  return { factory, databases, log };
}

const stored = (name = "laptop"): StoredKey => ({ name, fingerprint: "f".repeat(64), publicKey: "cHVibGlj", privateKey: { type: "private" } as unknown as CryptoKey });

let idb: ReturnType<typeof fakeIndexedDb>;
const install = (fail: Parameters<typeof fakeIndexedDb>[0] = {}) => {
  idb = fakeIndexedDb(fail);
  vi.stubGlobal("indexedDB", idb.factory);
};
beforeEach(() => install());
afterEach(() => vi.unstubAllGlobals());

describe("the approval key kept in IndexedDB", () => {
  it("has no key before one is put, and creates the database and its store on the first use", async () => {
    const store = indexedDbStore();
    expect(await store.get()).toBeNull();
    expect(idb.log.opened).toEqual([{ name: "ap-approval", version: 1 }]);
    expect([...idb.databases.get("ap-approval")!.keys()]).toEqual(["keys"]);
  });

  it("gives back the key that was put, as it is (the private half is not copied into text)", async () => {
    const store = indexedDbStore();
    const key = stored();
    await store.put(key);
    expect(await store.get()).toBe(key);
  });

  it("keeps one key: a second put replaces the first", async () => {
    const store = indexedDbStore();
    await store.put(stored("first"));
    await store.put(stored("second"));
    expect((await store.get())?.name).toBe("second");
    expect([...idb.databases.get("ap-approval")!.get("keys")!.keys()]).toEqual(["current"]);
  });

  it("forgets the key when cleared", async () => {
    const store = indexedDbStore();
    await store.put(stored());
    await store.clear();
    expect(await store.get()).toBeNull();
  });

  it("is still there for a store opened later, as after a page reload", async () => {
    await indexedDbStore().put(stored("kept"));
    expect((await indexedDbStore().get())?.name).toBe("kept");
  });

  it("reads with a read-only transaction and writes with a read-write one", async () => {
    const store = indexedDbStore();
    await store.get();
    await store.put(stored());
    await store.clear();
    expect(idb.log.modes).toEqual(["readonly", "readwrite", "readwrite"]);
  });

  it("closes the database after every operation, also when the request fails", async () => {
    const store = indexedDbStore();
    await store.put(stored());
    await store.get();
    expect(idb.log.closed).toBe(2);
    install({ request: true });
    await expect(indexedDbStore().get()).rejects.toThrow("request failed");
    expect(idb.log.closed).toBe(1);
  });

  it("fails with the browser's error when the database cannot be opened, instead of answering as if there were no key", async () => {
    install({ open: true });
    await expect(indexedDbStore().get()).rejects.toThrow("open failed");
    await expect(indexedDbStore().put(stored())).rejects.toThrow("open failed");
  });

  it("lets the key of a browser be made, kept and used to sign through the store, and found by a new page", async () => {
    const first = createApprovalKeys(indexedDbStore());
    const made = await first.generate("laptop");
    const later = createApprovalKeys(indexedDbStore());
    expect((await later.current())?.fingerprint).toBe(made.fingerprint);
    expect((await later.sign("payload"))?.keyId).toBe(made.fingerprint);
    await later.forget();
    expect(await first.current()).toBeNull();
  });
});
