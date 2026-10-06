import { useState, type ReactNode } from "react";
import { ApiError } from "../api/client";
import { Field } from "../components/ui";
import { useErrorText, useI18n, type Key } from "../i18n";

export interface CodeGate {
  /** Runs an action: the first attempt goes without a code; once the API asks for one, the next carries it. True when it succeeded. */
  attempt: (act: (code?: string) => Promise<void>) => Promise<boolean>;
  busy: boolean;
  error: string | null;
  /** The code field, once the API has asked for it; null before. */
  field: ReactNode;
}

/**
 * An owner's action that may need a two-step code (ADR-0005): a shell approval answers 403 "mfa_required", a grant or job
 * approval 403 "reauth_required" (the sign-in is not recent). The code field appears on that answer; a wrong code is
 * reported as such. Any other failure is shown as it is.
 */
export function useCodeGate(needCode: "mfa_required" | "reauth_required", hint: Key): CodeGate {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [asking, setAsking] = useState(false);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const attempt = async (act: (code?: string) => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await act(asking && code.trim() ? code.trim() : undefined);
      setAsking(false);
      setCode("");
      return true;
    } catch (e) {
      if (e instanceof ApiError && e.code === needCode) {
        setError(asking ? t("errors.invalid_code") : null);
        setAsking(true);
      } else {
        setError(errorText(e));
      }
      return false;
    } finally {
      setBusy(false);
    }
  };

  const field = asking ? (
    <Field label={t("remote.codeLabel")} hint={t(hint)} value={code} inputMode="numeric" autoComplete="one-time-code" autoFocus
      onChange={(e) => setCode(e.target.value)} />
  ) : null;
  return { attempt, busy, error, field };
}
