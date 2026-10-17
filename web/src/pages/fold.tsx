import { createContext, useCallback, useContext, useMemo, useState } from "react";
import type { ReactNode, SyntheticEvent } from "react";
import { isOpen, type OpenKeys, withOpen } from "../lib/fold";

interface FoldState { open: OpenKeys; setOpen: (keys: readonly string[], on: boolean) => void }

const FoldContext = createContext<FoldState | null>(null);

/** Keeps which accordions of a list are open above the lines, so a line that is drawn again or replaced by a live update stays as the reader left it. */
export function FoldProvider({ children }: { children: ReactNode }) {
  const [open, set] = useState<OpenKeys>(new Set());
  const setOpen = useCallback((keys: readonly string[], on: boolean) => set((s) => withOpen(s, keys, on)), []);
  const value = useMemo(() => ({ open, setOpen }), [open, setOpen]);
  return <FoldContext.Provider value={value}>{children}</FoldContext.Provider>;
}

/** The `open` and `onToggle` of a `<details>` made of the lines `keys`; outside a provider the line keeps its own state. */
export function useFold(keys: readonly string[]): { open: boolean; onToggle: (e: SyntheticEvent<HTMLDetailsElement>) => void } {
  const shared = useContext(FoldContext);
  const [own, setOwn] = useState(false);
  const open = shared ? isOpen(shared.open, keys) : own;
  const onToggle = (e: SyntheticEvent<HTMLDetailsElement>) => {
    const on = e.currentTarget.open;
    if (on === open) return;
    if (shared) shared.setOpen(keys, on);
    else setOwn(on);
  };
  return { open, onToggle };
}
