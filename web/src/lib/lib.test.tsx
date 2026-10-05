import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ApiError, request } from "../api/client";
import { en } from "../i18n/en";
import { tr } from "../i18n/tr";
import { translate } from "../i18n";
import { FakeEventSource, mockApi } from "../test/helpers";
import { age, bytes, count, date, dateTime, remaining, time, usd } from "./format";
import { debounce, useLive } from "./live";
import { useNow } from "./useNow";

describe("format", () => {
  it("writes dates as dd/mm/yyyy in local time", () => {
    const iso = new Date(2026, 9, 3, 7, 5, 9).toISOString();
    expect(date(iso)).toBe("03/10/2026");
    expect(dateTime(iso)).toBe("03/10/2026 07:05");
    expect(time(iso)).toBe("07:05:09");
    expect(date(null)).toBe("");
    expect(dateTime(undefined)).toBe("");
    expect(time("")).toBe("");
  });

  it("gives short ages and falls back to the date after a week", () => {
    const now = Date.UTC(2026, 9, 3, 12);
    const units = en.time;
    expect(age(new Date(now - 10_000).toISOString(), now, units)).toBe("now");
    expect(age(new Date(now - 5 * 60_000).toISOString(), now, units)).toBe("5m");
    expect(age(new Date(now - 3 * 3_600_000).toISOString(), now, units)).toBe("3h");
    expect(age(new Date(now - 2 * 86_400_000).toISOString(), now, units)).toBe("2d");
    expect(age(new Date(now - 20 * 86_400_000).toISOString(), now, units)).toMatch(/^\d{2}\/\d{2}\/\d{4}$/);
  });

  it("counts the time left, and says nothing once it is gone or when it is not a date", () => {
    const now = Date.UTC(2026, 9, 3, 12);
    const units = en.time;
    const at = (ms: number) => new Date(now + ms).toISOString();
    expect(remaining(at(20_000), now, units)).toBe("1m");
    expect(remaining(at(24 * 60_000 - 1), now, units)).toBe("24m");
    expect(remaining(at(60 * 60_000), now, units)).toBe("1h 00m");
    expect(remaining(at(95 * 60_000), now, units)).toBe("1h 35m");
    expect(remaining(at(0), now, units)).toBeNull();
    expect(remaining(at(-5000), now, units)).toBeNull();
    expect(remaining("", now, units)).toBeNull();
    expect(remaining(null, now, units)).toBeNull();
    expect(remaining("not a date", now, units)).toBeNull();
  });

  it("shows money, counts and sizes", () => {
    expect(usd(1.234, "x")).toBe("$1.23");
    expect(usd(0.0012, "x")).toBe("$0.0012");
    expect(usd(0, "x")).toBe("$0.00");
    expect(usd(null, "cannot be measured")).toBe("cannot be measured");
    expect([count(999), count(1234), count(45_000), count(3_400_000)]).toEqual(["999", "1.2k", "45k", "3.4M"]);
    expect([bytes(10), bytes(2048), bytes(3 * 1024 * 1024)]).toEqual(["10 B", "2 KB", "3.0 MB"]);
  });
});

describe("i18n", () => {
  const keys = (o: object, prefix = ""): string[] =>
    Object.entries(o).flatMap(([k, v]) => (typeof v === "string" ? [prefix + k] : keys(v as object, `${prefix}${k}.`)));

  it("has every English key in Turkish, and no empty text", () => {
    expect(keys(tr)).toEqual(keys(en));
    for (const k of keys(tr)) expect(translate(tr, k)).not.toBe("");
  });

  it("fills placeholders and shows an unknown key rather than nothing", () => {
    expect(translate(en, "auth.passwordHint", { n: 10 })).toBe("At least 10 characters.");
    expect(translate(en, "sessions.permissionsWaiting", {})).toBe("{n} waiting for an answer");
    expect(translate(en, "no.such.key")).toBe("no.such.key");
  });
});

describe("api client", () => {
  it("sends the CSRF header on changes only and parses JSON", async () => {
    const calls = mockApi({ "GET /me": { body: { id: "1" } }, "POST /auth/logout": { status: 204 } });
    expect(await request("GET", "/me")).toEqual({ id: "1" });
    expect(await request("POST", "/auth/logout")).toBeUndefined();
    const [get, post] = vi.mocked(fetch).mock.calls;
    expect((get![1]!.headers as Record<string, string>)["X-CSRF"]).toBeUndefined();
    expect((post![1]!.headers as Record<string, string>)["X-CSRF"]).toBe("1");
    expect(calls.map((c) => c.method)).toEqual(["GET", "POST"]);
  });

  it("turns validation problems, titles and bare statuses into codes", async () => {
    mockApi({
      "POST /a": { status: 400, body: { errors: { email: ["invalid_email"] } } },
      "POST /b": { status: 401, body: { title: "invalid_credentials" } },
      "POST /c": { status: 409 },
      "POST /d": { status: 503 },
      "POST /e": { status: 418 },
    });
    const code = async (p: string) => (await request("POST", p).catch((e: ApiError) => e)) as ApiError;
    const a = await code("/a");
    expect([a.status, a.code, a.fields.email]).toEqual([400, "invalid_email", "invalid_email"]);
    expect((await code("/b")).code).toBe("invalid_credentials");
    expect((await code("/c")).code).toBe("conflict");
    expect((await code("/d")).code).toBe("server_error");
    expect((await code("/e")).code).toBe("request_failed");
    expect((await code("/zzz")).code).toBe("not_found");
  });
});

describe("live", () => {
  it("debounces bursts into one call", () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const run = debounce(fn, 100);
    run();
    run();
    vi.advanceTimersByTime(150);
    expect(fn).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });

  it("listens to the workspace stream and reconnects after a drop", () => {
    vi.useFakeTimers();
    vi.stubGlobal("EventSource", FakeEventSource);
    const seen: string[] = [];
    const { result, unmount } = renderHook(() => useLive("w1", (m) => seen.push(`${m.event}:${m.sessionId ?? ""}`)));
    const first = FakeEventSource.last!;
    expect(first.url).toBe("/api/workspaces/w1/stream");
    act(() => first.emit("ready", {}));
    expect(result.current).toBe(true);
    act(() => {
      first.emit("session", { sessionId: "s1" });
      first.emit("permission", "not json");
    });
    expect(seen).toEqual(["session:s1", "permission:"]);
    act(() => first.onerror!());
    expect(result.current).toBe(false);
    act(() => vi.advanceTimersByTime(3_000));
    expect(FakeEventSource.last).not.toBe(first);
    unmount();
    expect(FakeEventSource.last!.closed).toBe(true);
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });
});

describe("useNow", () => {
  it("redraws on every tick and stops when the page goes away", () => {
    vi.useFakeTimers();
    try {
      vi.setSystemTime(new Date(Date.UTC(2026, 9, 3, 12)));
      const { result, unmount } = renderHook(() => useNow(1000));
      const first = result.current;
      act(() => {
        vi.advanceTimersByTime(3000);
      });
      expect(result.current - first).toBe(3000);
      unmount();
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});
