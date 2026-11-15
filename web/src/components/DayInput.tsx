import { useState, type ChangeEvent, type InputHTMLAttributes } from "react";
import { useI18n } from "../i18n";
import { day, parseDay } from "../lib/format";
import { Field } from "./ui";

/** What the form keeps for typed text: "" when empty, "2026-10-05" for a real day, the text itself when it is none (the form then refuses it). */
const draftOf = (text: string): string => {
  const trimmed = text.trim();
  return trimmed === "" ? "" : (parseDay(trimmed) ?? trimmed);
};

/**
 * The props of a text box for a calendar day: it shows and takes dd/mm/yyyy whatever the browser's locale is (global #12; a native date input
 * follows the locale), while the form's value stays "yyyy-mm-dd". Text that is not a day is handed up as it is, for the form's own check.
 */
function useDayText(value: string, onChange: (value: string) => void): InputHTMLAttributes<HTMLInputElement> {
  const { t } = useI18n();
  const [typed, setTyped] = useState<string | null>(null);
  // While the typed text still means the form's value it stays as typed (a half-typed day must not be rewritten); another value replaces it.
  const shown = typed !== null && draftOf(typed) === value ? typed : day(value);
  return {
    value: shown, inputMode: "numeric", maxLength: 10, placeholder: t("projects.dayFormat"), autoComplete: "off",
    onChange: (e: ChangeEvent<HTMLInputElement>) => {
      setTyped(e.target.value);
      onChange(draftOf(e.target.value));
    },
  };
}

/** A labelled day field, like <Field> with type="date" but always dd/mm/yyyy. */
export function DayField({ label, value, onChange, error }: { label: string; value: string; onChange: (value: string) => void; error?: string }) {
  return <Field label={label} error={error} {...useDayText(value, onChange)} />;
}

/** A bare day box for rows that carry their own label (aria-label). */
export function DayInput({ value, onChange, label }: { value: string; onChange: (value: string) => void; label: string }) {
  return <input className="input" aria-label={label} {...useDayText(value, onChange)} />;
}
