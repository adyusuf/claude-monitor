import { useState } from "react";
import { api } from "../api/endpoints";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

const PART = /^[A-Za-z0-9._-]{1,32}$/;
export const TAGS_MAX = 10;
export const NOTE_MAX = 2000;

/** "project=shop region=eu" (spaces or commas between pairs) as pairs; null when a pair is malformed or there are too many. */
export function parseTags(text: string): Record<string, string> | null {
  const tags: Record<string, string> = {};
  for (const pair of text.split(/[\s,]+/).filter(Boolean)) {
    const at = pair.indexOf("=");
    const key = at < 0 ? "" : pair.slice(0, at);
    const value = at < 0 ? "" : pair.slice(at + 1);
    if (!PART.test(key) || !PART.test(value)) return null;
    tags[key] = value;
  }
  return Object.keys(tags).length <= TAGS_MAX ? tags : null;
}

export const tagsText = (tags: Record<string, string> | null | undefined): string =>
  Object.entries(tags ?? {}).sort(([a], [b]) => (a < b ? -1 : 1)).map(([k, v]) => `${k}=${v}`).join(" ");

/** True for text the API accepts as a note: at most 2000 characters, no control characters besides line breaks and tabs. */
export const noteOk = (note: string): boolean =>
  note.length <= NOTE_MAX && [...note].every((c) => {
    const code = c.charCodeAt(0);
    return (code >= 0x20 && code !== 0x7f) || c === "\n" || c === "\r" || c === "\t";
  });

/** The tags of a machine: everyone reads them, an admin edits them as "key=value" pairs (ADR-0008 §3a′). */
export function TagsEditor({ agentId, tags, canEdit, onChanged }: { agentId: string; tags: Record<string, string> | null | undefined; canEdit: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const stored = tagsText(tags);
  const [text, setText] = useState(stored);
  const [seen, setSeen] = useState(stored);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  if (seen !== stored) {
    setSeen(stored);
    setText(stored);
  }
  const parsed = parseTags(text);

  if (!canEdit) {
    return stored ? <ul className="chips" aria-label={t("machines.tags.title")}>{Object.entries(tags ?? {}).map(([k, v]) => <li key={k} className="badge">{k}={v}</li>)}</ul>
      : <p className="muted">{t("machines.tags.none")}</p>;
  }

  const save = async () => {
    if (!parsed) return;
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      await api.setAgentTags(agentId, parsed);
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
        <span className="field-label">{t("machines.tags.label")}</span>
        <input className="input" value={text} onChange={(e) => setText(e.target.value)} placeholder="project=shop region=eu" aria-invalid={parsed === null} />
      </label>
      <p className="muted small">{t("machines.tags.hint", { max: TAGS_MAX })}</p>
      {parsed === null ? <Notice kind="error">{t("errors.invalid_tag")}</Notice> : null}
      {error ? <Notice kind="error">{error}</Notice> : null}
      {saved ? <Notice kind="success">{t("machines.tags.saved")}</Notice> : null}
      <div className="row-actions"><Button type="submit" busy={busy} disabled={parsed === null}>{t("machines.tags.save")}</Button></div>
    </form>
  );
}

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
