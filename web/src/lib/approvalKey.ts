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

const toBase64 = (bytes: ArrayBuffer): string => {
  let text = "";
  for (const b of new Uint8Array(bytes)) text += String.fromCharCode(b);
  return btoa(text);
};

const hex = (bytes: ArrayBuffer): string => [...new Uint8Array(bytes)].map((b) => b.toString(16).padStart(2, "0")).join("");

/** The bytes signed, as text: the tag, the kind, the run, the target agent, the run's hash and the signed expiry in whole Unix seconds, one per line. */
export const payloadFor = (kind: string, id: string, targetAgentId: string, hash: string, notAfterIso: string): string =>
  `${TAG}\n${kind}\n${id.toLowerCase()}\n${targetAgentId.toLowerCase()}\n${hash}\n${Math.floor(Date.parse(notAfterIso) / 1000)}`;

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
const SLOT = "current";

/** IndexedDB keeps a CryptoKey as it is (structured clone), which is what lets the private half stay non-extractable. */
export function indexedDbStore(): KeyStore {
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
    get: async () => ((await run("readonly", (s) => s.get(SLOT))) as StoredKey | undefined) ?? null,
    put: async (key) => { await run("readwrite", (s) => s.put(key, SLOT)); },
    clear: async () => { await run("readwrite", (s) => s.delete(SLOT)); },
  };
}

const hasIndexedDb = (): boolean => {
  try {
    return typeof indexedDB !== "undefined" && indexedDB !== null;
  } catch {
    return false;
  }
};

/** The key of this browser: in IndexedDB when there is one, else for this page only. */
export const approvalKeys = createApprovalKeys(hasIndexedDb() ? indexedDbStore() : memoryStore());
