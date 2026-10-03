import { config } from "../config";

/** An API failure: the HTTP status and, when the API named it, an error code the pages translate. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly fields: Record<string, string>;

  constructor(status: number, code: string, fields: Record<string, string> = {}) {
    super(code);
    this.status = status;
    this.code = code;
    this.fields = fields;
  }
}

type Method = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

/** One call to the API on our own origin. Cookies go along; changes carry the CSRF header the API demands. */
export async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (method !== "GET") headers[config.csrfHeader] = "1";
  const response = await fetch(config.apiBase + path, {
    method,
    headers,
    credentials: "same-origin",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.ok) {
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }
  throw await toError(response);
}

async function toError(response: Response): Promise<ApiError> {
  let data: { title?: string; errors?: Record<string, string[]> } = {};
  try {
    data = await response.json();
  } catch {
    // no body or not JSON: the status alone says what happened
  }
  const fields: Record<string, string> = {};
  for (const [field, codes] of Object.entries(data.errors ?? {})) fields[field] = codes[0] ?? "invalid";
  const code = Object.values(fields)[0] ?? data.title ?? statusCode(response.status);
  return new ApiError(response.status, code, fields);
}

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
