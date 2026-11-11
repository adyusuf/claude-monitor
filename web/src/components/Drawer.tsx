import type { ReactNode } from "react";
import { Modal } from "./Modal";

/** A panel that slides in from the right over the page: the Modal (Escape, a click outside, focus kept inside), full height at the right edge. */
export function Drawer({ title, onClose, children }: { title: ReactNode; onClose: () => void; children: ReactNode }) {
  return <Modal title={title} onClose={onClose} side>{children}</Modal>;
}
