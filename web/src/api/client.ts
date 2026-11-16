import { config } from "../config";

/** An API failure: the HTTP status and, when the API named it, an error code the pages translate. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly fields: Record<string, string>;
  /** The whole problem body, for the extra values some answers carry (the two-step sign-in token). */
  readonly data: Record<string, unknown>;

  constructor(status: number, code: string, fields: Record<string, string> = {}, data: Record<string, unknown> = {}) {
    super(code);
    this.status = status;
    this.code = code;
    this.fields = fields;
    this.data = data;
  }
}

/** The language the person picked in the menu (before any is picked: none, and the browser's own header is sent). */
function chosenLanguage(): string | null {
  try {
    const saved = localStorage.getItem(config.languageKey);
    return saved && /^[a-z]{2}$/.test(saved) ? saved : null;
  } catch {
    return null; // storage unavailable
  }
}

type Method = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

/** The calls whose 401 is not an ended session: the session's own read, and sign-in steps (a wrong password or code answers 401). */
const sessionReadPath = "/me";
const signInPrefix = "/auth/";

const sessionEndedListeners = new Set<() => void>();

/** Hears when a call answers 401 because the session ended (expired or revoked) while a page was open; returns the unsubscribe. */
export function onSessionEnded(listener: () => void): () => void {
  sessionEndedListeners.add(listener);
  return () => {
    sessionEndedListeners.delete(listener);
  };
}

/** One call to the API on our own origin. Cookies go along; changes carry the CSRF header the API demands. */
export async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const response = await send(method, path, body);
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** The header a list answer carries when the API left rows out (F-077). */
const TRUNCATED_HEADER = "X-Truncated";

/** A list read, and whether the API cut it (the X-Truncated header). */
export async function requestList<T>(path: string): Promise<{ items: T[]; truncated: boolean }> {
  const response = await send("GET", path);
  const text = await response.text();
  return { items: (text ? JSON.parse(text) : []) as T[], truncated: response.headers.get(TRUNCATED_HEADER) === "true" };
}

/** Sends the call and gives back a successful answer; a failed one is thrown as an ApiError. */
async function send(method: Method, path: string, body?: unknown): Promise<Response> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (method !== "GET") headers[config.csrfHeader] = "1";
  const language = chosenLanguage();
  if (language) headers["Accept-Language"] = language; // the language picked in the menu, so the mails the API sends come in it
  const response = await fetch(config.apiBase + path, {
    method,
    headers,
    credentials: "same-origin",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.ok) return response;
  if (response.status === 401 && path !== sessionReadPath && !path.startsWith(signInPrefix)) {
    for (const listener of sessionEndedListeners) listener();
  }
  throw await toError(response);
}

async function toError(response: Response): Promise<ApiError> {
  let data: { title?: string; errors?: Record<string, string[]>; [key: string]: unknown } = {};
  try {
    data = await response.json();
  } catch {
    // no body or not JSON: the status alone says what happened
  }
  const fields: Record<string, string> = {};
  for (const [field, codes] of Object.entries(data.errors ?? {})) fields[field] = codes[0] ?? "invalid";
  const title = typeof data.title === "string" && API_CODE.test(data.title) ? data.title : undefined;
  const code = Object.values(fields)[0] ?? title ?? statusCode(response.status);
  return new ApiError(response.status, code, fields, data);
}

/** The shape of the codes the API puts in `title`; any other title (the framework's English text on a real 500) is not one. */
const API_CODE = /^[a-z][a-z0-9_]*$/;

function statusCode(status: number): string {
  switch (status) {
    case 401:
      return "unauthorized";
    case 403:
      return "forbidden";
    case 404:
      return "not_found";
    case 409:
      return "conflict";
    case 429:
      return "too_many_requests";
    default:
      return status >= 500 ? "server_error" : "request_failed";
  }
}
