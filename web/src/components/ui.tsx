import { useId, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from "react";
import type { SessionStatus } from "../api/types";
import { useI18n } from "../i18n";

export function Button({ variant = "primary", busy, children, ...rest }:
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "ghost" | "danger" | "subtle"; busy?: boolean }) {
  return (
    <button type="button" className={`btn btn-${variant}`} disabled={busy || rest.disabled} {...rest}>
      {busy ? <span className="btn-spin" aria-hidden /> : null}
      {children}
    </button>
  );
}

export function Field({ label, hint, error, id, ...input }: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string; error?: string }) {
  const generated = useId();
  const inputId = id ?? generated;
  const noteId = `${inputId}-note`;
  return (
    <div className="field">
      <label className="field-label" htmlFor={inputId}>{label}</label>
      <input id={inputId} className={error ? "input input-error" : "input"} aria-invalid={error ? true : undefined}
        aria-describedby={error || hint ? noteId : undefined} {...input} />
      {error ? <span id={noteId} className="field-error" role="alert">{error}</span> : hint ? <span id={noteId} className="field-hint">{hint}</span> : null}
    </div>
  );
}

export function Card({ title, actions, children, className }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`card ${className ?? ""}`}>
      {title || actions ? (
        <header className="card-head">
          {title ? <h2>{title}</h2> : <span />}
          {actions}
        </header>
      ) : null}
      {children}
    </section>
  );
}

export function StatusPill({ status }: { status: SessionStatus }) {
  const { t } = useI18n();
  return <span className={`pill pill-${status}`}><span className="dot" aria-hidden />{t(`status.${status}`)}</span>;
}

export function Notice({ kind = "info", children }: { kind?: "info" | "error" | "success"; children: ReactNode }) {
  return <div className={`notice notice-${kind}`} role={kind === "error" ? "alert" : "status"}>{children}</div>;
}

export function Spinner() {
  const { t } = useI18n();
  return <div className="spinner" role="status" aria-label={t("common.loading")}><span /></div>;
}

export function Empty({ title, hint, children }: { title: string; hint?: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <div className="empty-mark" aria-hidden>◎</div>
      <p className="empty-title">{title}</p>
      {hint ? <p className="muted">{hint}</p> : null}
      {children}
    </div>
  );
}

export function CopyText({ text }: { text: string }) {
  const { t } = useI18n();
  const [done, setDone] = useState(false);
  return (
    <div className="copy">
      <code>{text}</code>
      <Button variant="subtle" onClick={() => {
        void navigator.clipboard?.writeText(text);
        setDone(true);
      }}>{done ? t("common.copied") : t("common.copy")}</Button>
    </div>
  );
}

/** A JSON payload, collapsed to one line until opened. */
export function Json({ value }: { value: unknown }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const text = JSON.stringify(value, null, open ? 2 : 0) ?? "";
  return (
    <div className="json">
      <pre className={open ? "json-open" : "json-closed"}>{text}</pre>
      {text.length > 120 ? <button type="button" className="link" onClick={() => setOpen(!open)}>{open ? t("session.hide") : t("session.show")}</button> : null}
    </div>
  );
}
