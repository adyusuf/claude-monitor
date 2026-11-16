/**
 * The owner's approval key (ADR-0010 §4): an ECDSA P-256 key pair made by this browser with WebCrypto, the private half NON-extractable and kept in IndexedDB so it
 * never leaves the browser, not even through this code. The public half (SubjectPublicKeyInfo, base64) is registered with the API and pinned on the machines that
 * require signed approvals. What is signed is the exact payload the API and the target agent check (see ApprovalSigning in the contracts).
 */
export interface StoredKey { name: string; fingerprint: string; publicKey: string; privateKey: CryptoKey }

export interface KeyStore {
  get(): Promise<StoredKey | null>;
  put(key: StoredKey): Promise<void>;
  clear(): Promise<void>;
}

export const SIGNING = { name: "ECDSA", namedCurve: "P-256" } as const;
export const TAG = "ap-approval-v1";
export const TAG_V2 = "ap-approval-v2";

const toBase64 = (bytes: ArrayBuffer): string => {
  let text = "";
  for (const b of new Uint8Array(bytes)) text += String.fromCharCode(b);
  return btoa(text);
};

const hex = (bytes: ArrayBuffer): string => [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, "0")).join("");

/** The bytes signed, as text: the tag, the kind, the run, the target agent, the run's hash and the signed expiry in whole Unix seconds, one per line. */
export const payloadFor = (kind: string, id: string, targetAgentId: string, hash: string, notAfterIso: string): string =>
  `${TAG}\n${kind}\n${id.toLowerCase()}\n${targetAgentId.toLowerCase()}\n${hash}\n${Math.floor(Date.parse(notAfterIso) / 1000)}`;

/**
 * Version 2 (ADR-0020 §4 rule 6): the same lines under its own tag, then the run's repository key, which decides the run's access policy, so the
 * server cannot relabel a run after it was signed (ApprovalSigning.PayloadV2).
 */
export const payloadV2For = (kind: string, id: string, targetAgentId: string, hash: string, notAfterIso: string, projectKey: string): string =>
  `${TAG_V2}\n${kind}\n${id.toLowerCase()}\n${targetAgentId.toLowerCase()}\n${hash}\n${Math.floor(Date.parse(notAfterIso) / 1000)}\n${projectKey}`;

/** What a run's approval signs: version 2 exactly when the run names a repository, else version 1 (the API and the agent pick the same). */
export const runPayload = (run: { id: string; targetAgentId: string; expiresAt: string; projectKey?: string | null }, hash: string): string =>
  run.projectKey ? payloadV2For("run", run.id, run.targetAgentId, hash, run.expiresAt, run.projectKey) : payloadFor("run", run.id, run.targetAgentId, hash, run.expiresAt);

/** SHA-256 of the key's SubjectPublicKeyInfo as lowercase hex: what a person compares with what the machine prints. */
export const fingerprintOf = async (spkiBase64: string): Promise<string> => {
  const bytes = Uint8Array.from(atob(spkiBase64), (c) => c.charCodeAt(0));
  return hex(await crypto.subtle.digest("SHA-256", bytes));
};

export function createApprovalKeys(store: KeyStore) {
  return {
    /** The key this browser holds, or null. */
    current: () => store.get(),

    /** Makes a new key pair (replacing nothing: the caller clears the old one first), keeps the private half and returns what is stored. */
    async generate(name: string): Promise<StoredKey> {
      const pair = await crypto.subtle.generateKey(SIGNING, false, ["sign", "verify"]);
      const publicKey = toBase64(await crypto.subtle.exportKey("spki", pair.publicKey));
      const key: StoredKey = { name, fingerprint: await fingerprintOf(publicKey), publicKey, privateKey: pair.privateKey };
      await store.put(key);
      return key;
    },

    /** Signs a payload with the held key: the 64-byte IEEE P1363 signature as base64, with the key's id; null when this browser holds no key. */
    async sign(payload: string): Promise<{ keyId: string; signature: string } | null> {
      const key = await store.get();
      if (!key) return null;
      const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key.privateKey, new TextEncoder().encode(payload));
      return { keyId: key.fingerprint, signature: toBase64(signature) };
    },

    forget: () => store.clear(),
  };
}

/** A store that lives only as long as the page: for tests, and for a browser that has no IndexedDB (the key is then made again after a reload). */
export function memoryStore(): KeyStore {
  let held: StoredKey | null = null;
  return { get: async () => held, put: async (k) => { held = k; }, clear: async () => { held = null; } };
}

const DB = "ap-approval";
const OBJECT = "keys";
/** The one browser-wide slot of the keys made before F-190: whoever signed in next used it. Read now only to hand a key to its owner. */
export const LEGACY_SLOT = "current";
/** Where keys go while nobody is signed in (nothing is signed then; tests that do not sign in use it). */
export const SIGNED_OUT_SLOT = "signed-out";
/** Each person's own slot (F-190): another account on the same browser never sees, offers or signs with this key. */
export const userSlot = (userId: string): string => `user:${userId}`;

/** IndexedDB keeps a CryptoKey as it is (structured clone), which is what lets the private half stay non-extractable. */
export function indexedDbStore(slot: string = LEGACY_SLOT): KeyStore {
  const open = () => new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open(DB, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(OBJECT);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  const run = async <T,>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> => {
    const db = await open();
    try {
      return await new Promise<T>((resolve, reject) => {
        const request = action(db.transaction(OBJECT, mode).objectStore(OBJECT));
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
    } finally {
      db.close();
    }
  };
  return {
    get: async () => ((await run("readonly", (s) => s.get(slot))) as StoredKey | undefined) ?? null,
    put: async (key) => { await run("readwrite", (s) => s.put(key, slot)); },
    clear: async () => { await run("readwrite", (s) => s.delete(slot)); },
  };
}

/** One page-lived store per slot, for a browser without IndexedDB. */
export function memoryStores(): (slot: string) => KeyStore {
  const stores = new Map<string, KeyStore>();
  return (slot) => {
    let store = stores.get(slot);
    if (!store) stores.set(slot, (store = memoryStore()));
    return store;
  };
}

/**
 * The key store of the person signed in now (F-190): each account has its own slot, so a key made by one person is never read, offered for
 * registration or used to sign by the next account on the same browser; signed out, the store is an empty slot of its own. A key made before
 * this (the old browser-wide slot) is handed to the person signed in only when it is one of their live registered keys, else it stays where it is.
 */
export function userScopedStore(storeFor: (slot: string) => KeyStore) {
  let slot = SIGNED_OUT_SLOT;
  const current = () => storeFor(slot);
  return {
    get: () => current().get(),
    put: (key: StoredKey) => current().put(key),
    clear: () => current().clear(),
    /** Who is signed in now; null when nobody is. */
    useFor(userId: string | null) {
      slot = userId ? userSlot(userId) : SIGNED_OUT_SLOT;
    },
    /** Moves the old browser-wide key to the signed-in person's slot when <code>isLive</code> says it is theirs; true when it moved. */
    async adoptLegacy(isLive: (fingerprint: string) => Promise<boolean>): Promise<boolean> {
      const mine = current();
      if (slot === SIGNED_OUT_SLOT || (await mine.get()) !== null) return false;
      const legacy = storeFor(LEGACY_SLOT);
      const old = await legacy.get();
      if (!old || !(await isLive(old.fingerprint))) return false;
      await mine.put(old);
      await legacy.clear();
      return true;
    },
  };
}

const hasIndexedDb = (): boolean => {
  try {
    return typeof indexedDB !== "undefined" && indexedDB !== null;
  } catch {
    return false;
  }
};

/** The signed-in person's key on this browser (the session sets who that is): in IndexedDB when there is one, else for this page only. */
export const approvalKeyScope = userScopedStore(hasIndexedDb() ? (slot) => indexedDbStore(slot) : memoryStores());
export const approvalKeys = createApprovalKeys(approvalKeyScope);
