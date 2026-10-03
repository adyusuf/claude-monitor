import { useEffect, useState, type ReactNode } from "react";
import { NavLink, Outlet, useNavigate, useParams } from "react-router";
import { useSession } from "../auth/session";
import { config } from "../config";
import { useI18n, type Language } from "../i18n";

type Theme = "light" | "dark" | "system";

function storedTheme(): Theme {
  try {
    const t = localStorage.getItem(config.themeKey);
    return t === "light" || t === "dark" ? t : "system";
  } catch {
    return "system";
  }
}

export function useTheme(): [Theme, (t: Theme) => void] {
  const [theme, setTheme] = useState<Theme>(storedTheme);
  useEffect(() => {
    if (theme === "system") document.documentElement.removeAttribute("data-theme");
    else document.documentElement.setAttribute("data-theme", theme);
    try {
      localStorage.setItem(config.themeKey, theme);
    } catch {
      // storage unavailable
    }
  }, [theme]);
  return [theme, setTheme];
}

/** The signed-in frame: workspace switcher and navigation on the left, the page on the right. */
export function Layout() {
  const { me, signOut } = useSession();
  const { t, language, setLanguage } = useI18n();
  const [theme, setTheme] = useTheme();
  const navigate = useNavigate();
  const { ws } = useParams();
  const workspaces = me?.workspaces ?? [];
  const current = workspaces.find((w) => w.id === ws) ?? workspaces[0];
  const [menuOpen, setMenuOpen] = useState(false);

  const link = (to: string, label: string, icon: ReactNode) => (
    <NavLink to={to} className={({ isActive }) => (isActive ? "nav-item active" : "nav-item")} onClick={() => setMenuOpen(false)}>
      <span className="nav-icon" aria-hidden>{icon}</span>
      {label}
    </NavLink>
  );

  return (
    <div className="shell">
      <aside className={menuOpen ? "sidebar open" : "sidebar"}>
        <div className="brand">
          <span className="brand-mark" aria-hidden>◉</span>
          <span>{t("app.name")}</span>
        </div>
        {current ? (
          <label className="switcher">
            <span className="sr-only">Workspace</span>
            <select value={current.id} onChange={(e) => e.target.value === "new" ? navigate("/workspaces/new") : navigate(`/w/${e.target.value}/sessions`)}>
              {workspaces.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
              <option value="new">+ {t("nav.newWorkspace")}</option>
            </select>
          </label>
        ) : null}
        {current ? (
          <nav className="nav">
            {link(`/w/${current.id}/sessions`, t("nav.sessions"), "▤")}
            {link(`/w/${current.id}/machines`, t("nav.machines"), "▣")}
            {link(`/w/${current.id}/members`, t("nav.members"), "◍")}
            {current.role === "owner" || current.role === "admin" ? link(`/w/${current.id}/settings`, t("nav.settings"), "⚙") : null}
            {link("/download", t("nav.download"), "↓")}
          </nav>
        ) : null}
        <div className="sidebar-foot">
          <NavLink to="/account" className="who" onClick={() => setMenuOpen(false)}>
            <span className="avatar" aria-hidden>{(me?.displayName ?? "?").slice(0, 1).toUpperCase()}</span>
            <span className="who-name">{me?.displayName}</span>
          </NavLink>
          <div className="prefs">
            <select aria-label={t("nav.language")} value={language} onChange={(e) => setLanguage(e.target.value as Language)}>
              <option value="en">English</option>
              <option value="tr">Türkçe</option>
            </select>
            <select aria-label={t("nav.theme")} value={theme} onChange={(e) => setTheme(e.target.value as Theme)}>
              <option value="system">◐</option>
              <option value="light">☀</option>
              <option value="dark">☾</option>
            </select>
          </div>
          <button type="button" className="link" onClick={async () => {
            await signOut();
            navigate("/login");
          }}>{t("nav.signOut")}</button>
        </div>
      </aside>
      <div className="main">
        <button type="button" className="menu-toggle" aria-label="Menu" onClick={() => setMenuOpen(!menuOpen)}>☰</button>
        <Outlet />
      </div>
    </div>
  );
}

/** The frame of the signed-out pages: a centred card. */
export function AuthFrame({ title, children }: { title: string; children: ReactNode }) {
  const { t } = useI18n();
  return (
    <div className="auth">
      <div className="auth-card">
        <div className="brand auth-brand"><span className="brand-mark" aria-hidden>◉</span><span>{t("app.name")}</span></div>
        <p className="muted auth-tagline">{t("app.tagline")}</p>
        <h1>{title}</h1>
        {children}
      </div>
    </div>
  );
}
