import { useId, type ReactNode } from "react";
import type { Member } from "../api/types";
import { Button } from "../components/ui";
import { useI18n } from "../i18n";
import { isWebLink } from "../lib/projectOptions";
import type { FieldErrors } from "../lib/projectForm";

/** The translated message of a form field's error code, or undefined when the field is fine. */
export function useFieldText(errors: FieldErrors) {
  const { tx } = useI18n();
  return (field: string): string | undefined => errors[field] ? tx(`errors.${errors[field]}`) : undefined;
}

/** A labelled control that is not a plain text input: the label points at the control, the error sits under it. */
export function Labeled({ label, error, children }: { label: string; error?: string; children: (id: string, invalid: boolean) => ReactNode }) {
  const id = useId();
  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>{label}</label>
      {children(id, Boolean(error))}
      {error ? <span className="field-error" role="alert">{error}</span> : null}
    </div>
  );
}

/** A group of controls (checkboxes, rows) with a legend and an error line. */
export function Group({ legend, error, children }: { legend: string; error?: string; children: ReactNode }) {
  return (
    <fieldset className="field group">
      <legend className="field-label">{legend}</legend>
      {children}
      {error ? <span className="field-error" role="alert">{error}</span> : null}
    </fieldset>
  );
}

/** An address from a person: a link only when it starts with http:// or https://, plain text otherwise (never a script or data address). */
export function WebText({ value }: { value: string }) {
  return isWebLink(value) ? <a href={value.trim()} target="_blank" rel="noopener noreferrer">{value}</a> : <span>{value}</span>;
}

export function Chips({ items, label }: { items: string[]; label: string }) {
  return <ul className="chip-list" aria-label={label}>{items.map((item) => <li key={item} className="badge">{item}</li>)}</ul>;
}

/** Short lines a person edits one by one (the definitions of ready and done). */
export function LineList({ label, lines, onChange, error, max }: {
  label: string; lines: string[]; onChange: (lines: string[]) => void; error?: string; max: number;
}) {
  const { t } = useI18n();
  return (
    <Group legend={label} error={error}>
      {lines.map((line, i) => (
        <div key={i} className="row-input">
          <input className="input" aria-label={`${label} ${i + 1}`} value={line} onChange={(e) => onChange(lines.map((l, j) => j === i ? e.target.value : l))} />
          <Button variant="subtle" aria-label={`${t("projects.removeRow")} ${label} ${i + 1}`} onClick={() => onChange(lines.filter((_, j) => j !== i))}>×</Button>
        </div>
      ))}
      {lines.length < max ? <div><Button variant="ghost" onClick={() => onChange([...lines, ""])}>{t("projects.addLine")}</Button></div> : null}
    </Group>
  );
}

/** The people who can own a project or an application: workspace members with at least the member role (and the current one, whoever they are). */
export const workers = (members: Member[], keep?: string | null): Member[] =>
  members.filter((m) => m.role !== "viewer" || m.userId === keep);
