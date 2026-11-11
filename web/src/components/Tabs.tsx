import { useId, useState, type KeyboardEvent, type ReactNode } from "react";

export interface TabDef {
  id: string;
  label: ReactNode;
  content: ReactNode;
  /** A tab that does not apply to this person is left out, not greyed. */
  hidden?: boolean;
}

/**
 * A strip of tabs over one visible panel; the arrow keys move between tabs. Every panel stays mounted and the others are only hidden, so what a
 * person typed into a form on one tab is still there when they come back (the page held all of it at once before it was split into tabs).
 */
export function Tabs({ tabs, initial, label }: { tabs: TabDef[]; initial?: string; label: string }) {
  const base = useId();
  const visible = tabs.filter((tab) => !tab.hidden);
  const [selected, setSelected] = useState(initial ?? visible[0]?.id);
  const current = visible.find((tab) => tab.id === selected) ?? visible[0];
  if (!current) return null;

  const move = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
    if (step === 0) return;
    event.preventDefault();
    const next = visible[(index + step + visible.length) % visible.length];
    if (!next) return;
    setSelected(next.id);
    document.getElementById(`${base}-tab-${next.id}`)?.focus();
  };

  return (
    <div className="tabs">
      <div className="segmented" role="tablist" aria-label={label}>
        {visible.map((tab, index) => {
          const on = tab.id === current.id;
          return (
            <button key={tab.id} id={`${base}-tab-${tab.id}`} type="button" role="tab" aria-selected={on}
              aria-controls={`${base}-panel-${tab.id}`} tabIndex={on ? 0 : -1} className={on ? "seg active" : "seg"}
              onClick={() => setSelected(tab.id)} onKeyDown={(event) => move(event, index)}>
              {tab.label}
            </button>
          );
        })}
      </div>
      {visible.map((tab) => (
        <div key={tab.id} role="tabpanel" id={`${base}-panel-${tab.id}`} aria-labelledby={`${base}-tab-${tab.id}`} hidden={tab.id !== current.id} className="tab-panel">
          {tab.content}
        </div>
      ))}
    </div>
  );
}
