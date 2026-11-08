import { useEffect, useState, type ReactNode } from "react";
import { NavLink, Outlet, useLocation, useNavigate, useParams } from "react-router";
import { api } from "../api/endpoints";
import { useSession } from "../auth/session";
import { config } from "../config";
import { languages, useI18n, type Language } from "../i18n";
import { usePendingApprovals } from "../lib/pending";
import { BrandMark, Icon, type IconName } from "./icons";

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
  const pending = usePendingApprovals(current?.id, useLocation().pathname);

  const link = (to: string, label: string, icon: IconName, badge = 0) => (
    <NavLink to={to} className={({ isActive }) => (isActive ? "nav-item active" : "nav-item")} onClick={() => setMenuOpen(false)}>
      <span className="nav-icon" aria-hidden><Icon name={icon} /></span>
      {label}
      {badge > 0 ? <span className="badge warn nav-badge" title={t("remote.pendingBadge", { n: badge })}>{badge}</span> : null}
    </NavLink>
  );

  return (
    <div className="shell">
      <aside className={menuOpen ? "sidebar open" : "sidebar"}>
        <div className="brand">
          <BrandMark />
          <span>{t("app.name")}</span>
        </div>
        {current ? (
          <label className="switcher">
            <span className="sr-only">Workspace</span>
            <select value={current.id} onChange={(e) => e.target.value === "new" ? navigate("/workspaces/new") : navigate(`/w/${e.target.value}/projects`)}>
              {workspaces.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
              <option value="new">+ {t("nav.newWorkspace")}</option>
            </select>
          </label>
        ) : null}
        {current ? (
          <nav className="nav">
            {link(`/w/${current.id}/projects`, t("nav.projects"), "projects")}
            {link(`/w/${current.id}/sessions`, t("nav.sessions"), "sessions")}
            {link(`/w/${current.id}/machines`, t("nav.machines"), "machines", pending)}
            {link(`/w/${current.id}/runs`, t("nav.runs"), "runs")}
            {link(`/w/${current.id}/members`, t("nav.members"), "members")}
            {link(`/w/${current.id}/integrations`, t("nav.integrations"), "integrations")}
            {current.role === "owner" || current.role === "admin" ? link(`/w/${current.id}/settings`, t("nav.settings"), "settings") : null}
            {me?.serverAdmin ? link("/server/integrations", t("nav.serverSettings"), "server") : null}
            {link("/download", t("nav.download"), "download")}
          </nav>
        ) : null}
        <div className="sidebar-foot">
          <NavLink to="/two-step" className="who-link" onClick={() => setMenuOpen(false)}>
            <Icon name="lock" /> {t("twoStep.nav")}
            {me && !me.mfaEnabled ? <span className="badge warn">{t("twoStep.navOff")}</span> : null}
          </NavLink>
          <NavLink to="/account" className="who" onClick={() => setMenuOpen(false)}>
            <span className="avatar" aria-hidden>{(me?.displayName ?? "?").slice(0, 1).toUpperCase()}</span>
            <span className="who-name">{me?.displayName}</span>
          </NavLink>
          <div className="prefs">
            <select aria-label={t("nav.language")} value={language} onChange={(e) => {
              const next = e.target.value as Language;
              setLanguage(next);
              if (me) void api.setMyLanguage(next).catch(() => undefined); // kept on the account for the mails; the menu works without it
            }}>
              {languages.map((l) => <option key={l.code} value={l.code}>{l.label}</option>)}
            </select>
            <select aria-label={t("nav.theme")} value={theme} onChange={(e) => setTheme(e.target.value as Theme)}>
              <option value="system">{t("nav.themeSystem")}</option>
              <option value="light">{t("nav.themeLight")}</option>
              <option value="dark">{t("nav.themeDark")}</option>
            </select>
          </div>
          <button type="button" className="link" onClick={async () => {
            await signOut();
            navigate("/login");
          }}>{t("nav.signOut")}</button>
        </div>
      </aside>
      <div className="main-col">
        <div className="topbar">
          <button type="button" className="menu-toggle" aria-label="Menu" onClick={() => setMenuOpen(!menuOpen)}><Icon name="menu" /></button>
          <span className="topbar-name">{t("app.name")}</span>
        </div>
        <div className="main">
          <Outlet />
        </div>
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
        <div className="brand auth-brand"><BrandMark /><span>{t("app.name")}</span></div>
        <p className="muted auth-tagline">{t("app.tagline")}</p>
        <h1>{title}</h1>
        {children}
      </div>
    </div>
  );
}
