import type { ReactNode } from "react";

/** One setting group on a settings page: what it is on the left, its controls on the right (stacked on a narrow screen). */
export function SettingsSection({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <section className="setting-row">
      <div className="setting-intro">
        <h2>{title}</h2>
        {hint ? <p className="muted">{hint}</p> : null}
      </div>
      <div className="setting-body">{children}</div>
    </section>
  );
}
