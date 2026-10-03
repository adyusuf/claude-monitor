import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import { config } from "../config";
import { en, type Dictionary } from "./en";
import { tr } from "./tr";

export type Language = "en" | "tr";
export const dictionaries: Record<Language, Dictionary> = { en, tr };

type Leaf<T, P extends string = ""> = {
  [K in keyof T & string]: T[K] extends string ? `${P}${K}` : Leaf<T[K], `${P}${K}.`>;
}[keyof T & string];
/** A dotted key into the dictionary, checked by the compiler ("auth.signIn"). */
export type Key = Leaf<Dictionary>;

/** Looks a dotted key up; "{name}" placeholders are filled from vars. An unknown key shows itself (never blank). */
export function translate(dict: Dictionary, key: string, vars?: Record<string, string | number>): string {
  let node: unknown = dict;
  for (const part of key.split(".")) node = (node as Record<string, unknown> | undefined)?.[part];
  const text = typeof node === "string" ? node : key;
  return vars ? text.replace(/\{(\w+)\}/g, (_, name: string) => String(vars[name] ?? `{${name}}`)) : text;
}

export function storedLanguage(): Language {
  try {
    const saved = localStorage.getItem(config.languageKey);
    if (saved === "en" || saved === "tr") return saved;
  } catch {
    // storage unavailable: fall back to the browser's language
  }
  return navigator.language.toLowerCase().startsWith("tr") ? "tr" : "en";
}

interface I18n {
  language: Language;
  /** The whole dictionary, for structured entries (the time units of format.age). */
  dict: Dictionary;
  setLanguage: (l: Language) => void;
  t: (key: Key, vars?: Record<string, string | number>) => string;
  /** For codes that come from the API ("errors." + code). */
  tx: (key: string, vars?: Record<string, string | number>) => string;
}

const I18nContext = createContext<I18n | null>(null);

export function I18nProvider({ children, initial }: { children: ReactNode; initial?: Language }) {
  const [language, setLanguageState] = useState<Language>(initial ?? storedLanguage());
  const setLanguage = useCallback((l: Language) => {
    setLanguageState(l);
    try {
      localStorage.setItem(config.languageKey, l);
    } catch {
      // storage unavailable: the choice lasts for this page only
    }
    document.documentElement.lang = l;
  }, []);
  const value = useMemo<I18n>(() => {
    const dict = dictionaries[language];
    return { language, dict, setLanguage, t: (k, v) => translate(dict, k, v), tx: (k, v) => translate(dict, k, v) };
  }, [language, setLanguage]);
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18n {
  const ctx = useContext(I18nContext);
  if (!ctx) throw new Error("useI18n outside I18nProvider");
  return ctx;
}

/** The translated message of an error: an API code when it has one, otherwise a network failure. */
export function useErrorText() {
  const { tx } = useI18n();
  return (error: unknown) => {
    const code = (error as { code?: string } | null)?.code;
    return code ? tx(`errors.${code}`) : tx("errors.network");
  };
}
