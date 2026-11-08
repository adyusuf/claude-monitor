import { useState } from "react";
import { api } from "../api/endpoints";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

export const NOTE_MAX = 2000;

/** True for text the API accepts as a note: at most 2000 characters, no control characters besides line breaks and tabs. */
export const noteOk = (note: string): boolean =>
  note.length <= NOTE_MAX && [...note].every((c) => {
    const code = c.charCodeAt(0);
    return (code >= 0x20 && code !== 0x7f) || c === "\n" || c === "\r" || c === "\t";
  });

/** The runbook note of a machine: every member reads it as plain text, an admin edits it. It is untrusted text and is never rendered as markup. */
export function NoteEditor({ agentId, note, canEdit, onChanged }: { agentId: string; note: string | null | undefined; canEdit: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const stored = note ?? "";
  const [text, setText] = useState(stored);
  const [seen, setSeen] = useState(stored);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  if (seen !== stored) {
    setSeen(stored);
    setText(stored);
  }

  if (!canEdit) return stored ? <p className="note-text">{stored}</p> : <p className="muted">{t("machines.note.none")}</p>;

  const save = async () => {
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      await api.setAgentNote(agentId, text.trim() === "" ? null : text);
      setSaved(true);
      onChanged();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="stack" onSubmit={(e) => {
      e.preventDefault();
      void save();
    }}>
      <label className="field">
        <span className="field-label">{t("machines.note.label")}</span>
        <textarea className="input" rows={5} value={text} onChange={(e) => setText(e.target.value)} aria-invalid={!noteOk(text)} />
      </label>
      <p className="muted small">{t("machines.note.hint", { max: NOTE_MAX })} ({text.length}/{NOTE_MAX})</p>
      {!noteOk(text) ? <Notice kind="error">{t("errors.invalid_note")}</Notice> : null}
      {error ? <Notice kind="error">{error}</Notice> : null}
      {saved ? <Notice kind="success">{t("machines.note.saved")}</Notice> : null}
      <div className="row-actions"><Button type="submit" busy={busy} disabled={!noteOk(text)}>{t("machines.note.save")}</Button></div>
    </form>
  );
}
