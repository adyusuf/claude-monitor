import { act, render, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { I18nProvider, storedLanguage, useI18n } from "./index";

const browserLanguage = (tag: string | undefined) => vi.spyOn(navigator, "language", "get").mockReturnValue(tag as string);

afterEach(() => {
  document.documentElement.removeAttribute("lang");
});

describe("storedLanguage", () => {
  it.each(["en", "tr", "de"] as const)("returns the saved language %s over the browser's", (saved) => {
    localStorage.setItem(config.languageKey, saved);
    browserLanguage("fr-FR");
    expect(storedLanguage()).toBe(saved);
  });

  it("ignores a saved value that is not one of ours and follows the browser", () => {
    localStorage.setItem(config.languageKey, "xx");
    browserLanguage("tr-TR");
    expect(storedLanguage()).toBe("tr");
  });

  it("follows the browser's language tag when nothing is saved", () => {
    browserLanguage("de-AT");
    expect(storedLanguage()).toBe("de");
  });

  it("falls back to English when nothing is saved and the browser's language is not ours", () => {
    browserLanguage("fr-FR");
    expect(storedLanguage()).toBe("en");
  });

  it("falls back to the browser's language when the storage cannot be read", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage unavailable");
    });
    browserLanguage("tr");
    expect(storedLanguage()).toBe("tr");
  });
});

describe("the language provider", () => {
  const wrapper = ({ children }: { children: ReactNode }) => <I18nProvider>{children}</I18nProvider>;

  it("starts in the saved language", () => {
    localStorage.setItem(config.languageKey, "de");
    const { result } = renderHook(() => useI18n(), { wrapper });
    expect(result.current.language).toBe("de");
  });

  it("saves a picked language, so the next visit starts in it, and sets the page's language", () => {
    browserLanguage("en-GB");
    const { result } = renderHook(() => useI18n(), { wrapper });
    act(() => result.current.setLanguage("tr"));
    expect(result.current.language).toBe("tr");
    expect(localStorage.getItem(config.languageKey)).toBe("tr");
    expect(document.documentElement.lang).toBe("tr");
    expect(storedLanguage()).toBe("tr");
  });

  it("changes the language for this page even when the storage refuses the write", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage unavailable");
    });
    const { result } = renderHook(() => useI18n(), { wrapper });
    act(() => result.current.setLanguage("de"));
    expect(result.current.language).toBe("de");
    expect(document.documentElement.lang).toBe("de");
  });

  it("prefers the language it was given over the saved one", () => {
    localStorage.setItem(config.languageKey, "de");
    const { result } = renderHook(() => useI18n(), { wrapper: ({ children }) => <I18nProvider initial="tr">{children}</I18nProvider> });
    expect(result.current.language).toBe("tr");
  });
});

describe("using the dictionary outside the provider", () => {
  it("fails loudly", () => {
    const Probe = () => {
      useI18n();
      return null;
    };
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    expect(() => render(<Probe />)).toThrow("useI18n outside I18nProvider");
  });
});
