import type { ReactNode } from "react";

/** The outline icons of the frame: one set, 16px, 1.5px stroke, in the text colour of where they stand (the sheet's `.icon`). */
const paths = {
  projects: <><rect x="2.5" y="2.5" width="4.5" height="4.5" rx="1" /><rect x="9" y="2.5" width="4.5" height="4.5" rx="1" /><rect x="2.5" y="9" width="4.5" height="4.5" rx="1" /><rect x="9" y="9" width="4.5" height="4.5" rx="1" /></>,
  sessions: <><path d="M2.5 4h11M2.5 8h11M2.5 12h7" /></>,
  machines: <><rect x="2" y="3" width="12" height="8" rx="1.5" /><path d="M5.5 13.5h5M8 11v2.5" /></>,
  members: <><circle cx="6" cy="5.5" r="2.5" /><path d="M1.5 13c.4-2.3 2.2-3.5 4.5-3.5s4.1 1.2 4.5 3.5" /><path d="M10.5 3.3a2.5 2.5 0 0 1 0 4.4M12 9.8c1.3.5 2.2 1.5 2.5 3.2" /></>,
  integrations: <><path d="M3 5.5h9l-2.5-2.5M13 10.5H4l2.5 2.5" /></>,
  settings: <><circle cx="8" cy="8" r="2" /><path d="M8 1.8v1.7M8 12.5v1.7M1.8 8h1.7M12.5 8h1.7M3.6 3.6l1.2 1.2M11.2 11.2l1.2 1.2M12.4 3.6l-1.2 1.2M4.8 11.2l-1.2 1.2" /></>,
  server: <><rect x="2.5" y="2.5" width="11" height="4" rx="1" /><rect x="2.5" y="9.5" width="11" height="4" rx="1" /><path d="M5 4.5h.01M5 11.5h.01" /></>,
  download: <><path d="M8 2.5v8M4.8 7.5 8 10.7l3.2-3.2M3 13.5h10" /></>,
  lock: <><rect x="3" y="7" width="10" height="6.5" rx="1.5" /><path d="M5.2 7V5.2a2.8 2.8 0 0 1 5.6 0V7" /></>,
  check: <><path d="M3.5 8.5 6.5 11.5 12.5 4.5" /></>,
  progress: <><circle cx="8" cy="8" r="5.5" /><path d="M8 2.5a5.5 5.5 0 0 1 0 11z" fill="currentColor" /></>,
  todo: <><circle cx="8" cy="8" r="5.5" /></>,
  branch: <><circle cx="4.5" cy="3.5" r="1.5" /><circle cx="4.5" cy="12.5" r="1.5" /><circle cx="11.5" cy="6" r="1.5" /><path d="M4.5 5v6M11.5 7.5c0 2.2-2.6 2.8-7 3.6" /></>,
  empty: <><circle cx="8" cy="8" r="6" /><circle cx="8" cy="8" r="2.8" /></>,
  menu: <><path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h11" /></>,
  edit: <><path d="M10.5 3 13 5.5 6 12.5H3.5V10z" /><path d="M9 4.5 11.5 7" /></>,
  close: <><path d="M4.5 4.5l7 7M11.5 4.5l-7 7" /></>,
  runs: <><rect x="2" y="3" width="12" height="10" rx="1.5" /><path d="M4.8 6.5 6.8 8l-2 1.5M8.5 10h3" /></>,
  approvals: <><path d="M8 1.8 13 3.6v4c0 3.1-2.1 5.4-5 6.6-2.9-1.2-5-3.5-5-6.6v-4L8 1.8Z" /><path d="m5.7 8 1.6 1.6L10.5 6.4" /></>,
} satisfies Record<string, ReactNode>;

export type IconName = keyof typeof paths;

export function Icon({ name }: { name: IconName }) {
  return <svg className="icon" viewBox="0 0 16 16" aria-hidden focusable="false">{paths[name]}</svg>;
}

/** Podium's mark: the three steps of a podium, the winner's in the middle. */
export function BrandMark() {
  return (
    <svg className="brand-mark" width="20" height="20" viewBox="0 0 20 20" aria-hidden focusable="false">
      <rect x="1" y="9" width="5.5" height="9" rx="1" fill="currentColor" opacity="0.55" />
      <rect x="7.25" y="3" width="5.5" height="15" rx="1" fill="currentColor" />
      <rect x="13.5" y="12" width="5.5" height="6" rx="1" fill="currentColor" opacity="0.75" />
    </svg>
  );
}
