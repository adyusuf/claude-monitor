import { render } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, Route, Routes } from "react-router";
import { vi } from "vitest";
import type { Me } from "../api/types";
import { SessionProvider } from "../auth/session";
import { I18nProvider } from "../i18n";

export interface Call { method: string; path: string; body: unknown }
type Answer = { status?: number; body?: unknown } | ((call: Call) => { status?: number; body?: unknown });

/** Replaces fetch with a table of "METHOD /path" answers (the path without /api and without the query) and records calls. */
export function mockApi(routes: Record<string, Answer>) {
  const calls: Call[] = [];
  vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
    const url = new URL(String(input), "http://test.local");
    const path = url.pathname.replace(/^\/api/, "");
    const method = init?.method ?? "GET";
    const call = { method, path: path + url.search, body: init?.body ? JSON.parse(String(init.body)) : undefined };
    calls.push(call);
    const answer = routes[`${method} ${path}`] ?? routes[`${method} ${path}${url.search}`];
    const { status = 200, body } = typeof answer === "function" ? answer(call) : answer ?? { status: 404, body: { title: "not_found" } };
    const empty = body === undefined || status === 204;
    return new Response(empty ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  });
  return calls;
}

export const ME: Me = {
  id: "u1",
  email: "ayse+test@gmail.com",
  displayName: "Örnek Kişi",
  hasPassword: true,
  providers: [],
  workspaces: [{ id: "w1", name: "Team", role: "owner" }, { id: "w2", name: "Other", role: "viewer" }],
};

/** Renders a page at a path with every provider the app has, in English. */
export function renderAt(path: string, routes: { path: string; element: ReactNode }[]) {
  return render(
    <I18nProvider initial="en">
      <MemoryRouter initialEntries={[path]}>
        <SessionProvider>
          <Routes>
            {routes.map((r) => <Route key={r.path} path={r.path} element={r.element} />)}
            <Route path="*" element={<div>elsewhere</div>} />
          </Routes>
        </SessionProvider>
      </MemoryRouter>
    </I18nProvider>,
  );
}

/** A stand-in for the browser's EventSource that the test drives. */
export class FakeEventSource {
  static last: FakeEventSource | null = null;
  url: string;
  listeners: Record<string, ((e: MessageEvent) => void)[]> = {};
  onerror: (() => void) | null = null;
  closed = false;

  constructor(url: string) {
    this.url = url;
    FakeEventSource.last = this;
  }

  addEventListener(name: string, fn: (e: MessageEvent) => void) {
    (this.listeners[name] ??= []).push(fn);
  }

  emit(name: string, data: unknown) {
    for (const fn of this.listeners[name] ?? []) fn(new MessageEvent(name, { data: typeof data === "string" ? data : JSON.stringify(data) }));
  }

  close() {
    this.closed = true;
  }
}
