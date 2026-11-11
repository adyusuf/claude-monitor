import { useEffect, useId, useRef, type ReactNode } from "react";
import { useI18n } from "../i18n";
import { Icon } from "./icons";

/** The dialogs that are open, the last on top: only the top one answers the keyboard (a drawer holds a dialog of its own). */
const open: object[] = [];

const FOCUSABLE = "a[href], button, input, select, textarea, [tabindex]:not([tabindex='-1'])";

/**
 * A dialog over the page: a title, a close button, Escape and a click on the backdrop close it. Focus moves into it when it opens and goes back
 * to where it was when it closes; the page behind does not scroll while it is open. `side` slides it in from the right edge, full height.
 */
export function Modal({ title, onClose, children, side }: { title: ReactNode; onClose: () => void; children: ReactNode; side?: boolean }) {
  const { t } = useI18n();
  const titleId = useId();
  const box = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  useEffect(() => {
    close.current = onClose;
  });

  useEffect(() => {
    const me = {};
    open.push(me);
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    box.current?.focus();
    const overflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKey = (e: KeyboardEvent) => {
      if (open[open.length - 1] !== me) return;
      if (e.key === "Escape") close.current();
      if (e.key !== "Tab" || !box.current) return;
      // Tab and Shift+Tab go round the dialog's own controls; the page behind is out of reach while it is open.
      const focusable = [...box.current.querySelectorAll<HTMLElement>(FOCUSABLE)].filter((el) => !el.hasAttribute("disabled"));
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!first || !last) return;
      const at = document.activeElement;
      if (e.shiftKey && (at === first || at === box.current)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && (at === last || !box.current.contains(at))) {
        e.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      open.splice(open.indexOf(me), 1);
      document.body.style.overflow = overflow;
      before?.focus();
    };
  }, []);

  return (
    <div className={side ? "modal-backdrop side" : "modal-backdrop"} onMouseDown={(e) => {
      if (e.target === e.currentTarget) onClose();
    }}>
      <div ref={box} className={side ? "modal side" : "modal"} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
        <header className="modal-head">
          <h2 id={titleId}>{title}</h2>
          <button type="button" className="modal-close" aria-label={t("common.close")} onClick={onClose}><Icon name="close" /></button>
        </header>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}
