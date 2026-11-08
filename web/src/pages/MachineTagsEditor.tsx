import { useState } from "react";
import { api } from "../api/endpoints";
import { Icon } from "../components/icons";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

const PART = /^[A-Za-z0-9._-]{1,32}$/;
export const TAGS_MAX = 100;

/** True for a key or a value the API accepts: letters, digits, dot, underscore and dash, 1 to 32 characters. */
export const tagPartOk = (text: string): boolean => PART.test(text);

/** The pairs sorted by key, the order every list of tags shows. */
export const sortedTags = (tags: Record<string, string> | null | undefined): [string, string][] =>
  Object.entries(tags ?? {}).sort(([a], [b]) => (a < b ? -1 : 1));

type Problem = "invalid" | "duplicate" | "full";

/**
 * The tag set with `from` taken out and key=value put in (a new pair when `from` is null), or why it cannot be: a malformed part, a key another
 * pair already has, or a new pair beyond the limit.
 */
export function withTag(tags: Record<string, string>, from: string | null, key: string, value: string): Record<string, string> | Problem {
  if (!tagPartOk(key) || !tagPartOk(value)) return "invalid";
  if (key !== from && key in tags) return "duplicate";
  const next = { ...tags };
  if (from !== null) delete next[from];
  next[key] = value;
  return Object.keys(next).length > TAGS_MAX ? "full" : next;
}

/** The two inputs of a pair (the key and the value), shared by the add row and a chip being edited. */
function PairInputs({ k, v, setK, setV }: { k: string; v: string; setK: (s: string) => void; setV: (s: string) => void }) {
  const { t } = useI18n();
  return (
    <>
      <input className="input tag-input" aria-label={t("machines.tags.key")} placeholder={t("machines.tags.key")} maxLength={32} value={k}
        aria-invalid={k !== "" && !tagPartOk(k)} onChange={(e) => setK(e.target.value.trim())} />
      <span aria-hidden>=</span>
      <input className="input tag-input" aria-label={t("machines.tags.value")} placeholder={t("machines.tags.value")} maxLength={32} value={v}
        aria-invalid={v !== "" && !tagPartOk(v)} onChange={(e) => setV(e.target.value.trim())} />
    </>
  );
}

/**
 * The tags of a machine (ADR-0008 §3a′): one pair per row, as a chip. Everyone reads them; an admin edits a pair or removes it from its chip
 * and adds pairs in the row below. Every change writes the whole set at once and reads the machine again.
 */
export function TagsEditor({ agentId, tags, canEdit, onChanged }: { agentId: string; tags: Record<string, string> | null | undefined; canEdit: boolean; onChanged: () => void }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const pairs = sortedTags(tags);
  const [editing, setEditing] = useState<string | null>(null);
  const [editKey, setEditKey] = useState("");
  const [editValue, setEditValue] = useState("");
  const [newKey, setNewKey] = useState("");
  const [newValue, setNewValue] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  if (!canEdit) {
    return pairs.length > 0
      ? <ul className="tag-rows" aria-label={t("machines.tags.title")}>{pairs.map(([k, v]) => <li key={k}><span className="badge">{k}={v}</span></li>)}</ul>
      : <p className="muted">{t("machines.tags.none")}</p>;
  }

  const problemText = (p: Problem): string => (p === "invalid" ? t("errors.invalid_tag") : p === "duplicate" ? t("machines.tags.duplicate") : t("machines.tags.full", { max: TAGS_MAX }));

  /** Writes the whole set; true when the API took it. */
  const write = async (next: Record<string, string> | Problem): Promise<boolean> => {
    setSaved(false);
    if (typeof next === "string") {
      setError(problemText(next));
      return false;
    }
    setBusy(true);
    setError(null);
    try {
      await api.setAgentTags(agentId, next);
      setSaved(true);
      onChanged();
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    } finally {
      setBusy(false);
    }
  };

  const current = Object.fromEntries(pairs);
  const startEdit = (k: string, v: string) => {
    setEditing(k);
    setEditKey(k);
    setEditValue(v);
    setError(null);
  };
  const remove = (k: string) => {
    const next = { ...current };
    delete next[k];
    void write(next);
  };
  const saveEdit = async (from: string) => {
    if (await write(withTag(current, from, editKey, editValue))) setEditing(null);
  };
  const add = async () => {
    if (await write(withTag(current, null, newKey, newValue))) {
      setNewKey("");
      setNewValue("");
    }
  };

  return (
    <div className="stack">
      {pairs.length > 0 ? (
        <ul className="tag-rows" aria-label={t("machines.tags.title")}>
          {pairs.map(([k, v]) => (
            <li key={k}>
              {editing === k ? (
                <form className="tag-edit" onSubmit={(e) => {
                  e.preventDefault();
                  void saveEdit(k);
                }}>
                  <PairInputs k={editKey} v={editValue} setK={setEditKey} setV={setEditValue} />
                  <Button type="submit" busy={busy} disabled={!tagPartOk(editKey) || !tagPartOk(editValue)}>{t("common.save")}</Button>
                  <Button variant="ghost" onClick={() => setEditing(null)}>{t("common.cancel")}</Button>
                </form>
              ) : (
                <span className="tag-chip">
                  {k}={v}
                  <button type="button" className="chip-x" aria-label={t("machines.tags.edit", { tag: `${k}=${v}` })} title={t("machines.tags.edit", { tag: `${k}=${v}` })}
                    disabled={busy} onClick={() => startEdit(k, v)}><Icon name="edit" /></button>
                  <button type="button" className="chip-x" aria-label={t("machines.tags.remove", { tag: `${k}=${v}` })} title={t("machines.tags.remove", { tag: `${k}=${v}` })}
                    disabled={busy} onClick={() => remove(k)}><Icon name="close" /></button>
                </span>
              )}
            </li>
          ))}
        </ul>
      ) : <p className="muted">{t("machines.tags.none")}</p>}
      <form className="tag-edit" onSubmit={(e) => {
        e.preventDefault();
        void add();
      }}>
        <PairInputs k={newKey} v={newValue} setK={setNewKey} setV={setNewValue} />
        <Button type="submit" busy={busy} disabled={!tagPartOk(newKey) || !tagPartOk(newValue)}>{t("machines.tags.add")}</Button>
      </form>
      <p className="muted small">{t("machines.tags.hint", { max: TAGS_MAX })}</p>
      {error ? <Notice kind="error">{error}</Notice> : null}
      {saved ? <Notice kind="success">{t("machines.tags.saved")}</Notice> : null}
    </div>
  );
}
