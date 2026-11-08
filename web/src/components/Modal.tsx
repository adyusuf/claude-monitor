import { useEffect, useId, useRef, type ReactNode } from "react";
import { useI18n } from "../i18n";
import { Icon } from "./icons";

/**
 * A dialog over the page: a title, a close button, Escape and a click on the backdrop close it. Focus moves into it when it opens and goes back
 * to where it was when it closes; the page behind does not scroll while it is open.
 */
export function Modal({ title, onClose, children }: { title: ReactNode; onClose: () => void; children: ReactNode }) {
  const { t } = useI18n();
  const titleId = useId();
  const box = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  useEffect(() => {
    close.current = onClose;
  });

  useEffect(() => {
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    box.current?.focus();
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close.current();
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.body.style.overflow = overflow;
      before?.focus();
    };
  }, []);

  return (
    <div className="modal-backdrop" onMouseDown={(e) => {
      if (e.target === e.currentTarget) onClose();
    }}>
      <div ref={box} className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
        <header className="modal-head">
          <h2 id={titleId}>{title}</h2>
          <button type="button" className="modal-close" aria-label={t("common.close")} onClick={onClose}><Icon name="close" /></button>
        </header>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}
