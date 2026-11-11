import { useEffect, useRef, type ReactNode } from "react";
import { useI18n } from "../i18n";
import { Button } from "./ui";

/** A panel that slides in from the right over the page. Escape or a click outside closes it; focus goes into it and comes back on close. */
export function Drawer({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const { t } = useI18n();
  const panel = useRef<HTMLElement>(null);
  const close = useRef(onClose);

  useEffect(() => {
    close.current = onClose;
  });

  useEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") close.current();
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      before?.focus?.();
    };
  }, []);

  return (
    <div className="drawer-root">
      <div className="drawer-backdrop" onClick={onClose} aria-hidden />
      <aside ref={panel} className="drawer" role="dialog" aria-modal="true" aria-label={title} tabIndex={-1}>
        <header className="card-head">
          <h2>{title}</h2>
          <Button variant="subtle" onClick={onClose}>{t("common.close")}</Button>
        </header>
        <div className="stack">{children}</div>
      </aside>
    </div>
  );
}
