import { useEffect, useState } from "react";
import { Link, Navigate, useSearchParams } from "react-router";
import { api, providerUrl } from "../api/endpoints";
import { useSession } from "../auth/session";
import { SettingsSection } from "../components/SettingsSection";
import { Card, CopyText, Notice } from "../components/ui";
import { config } from "../config";
import { useErrorText, useI18n } from "../i18n";
import { AccountDataCards } from "./AccountData";
import { ApprovalKeysCard } from "./ApprovalKeysCard";
import { InstallOptions } from "./InstallOptions";
import { MfaCard } from "./Mfa";
import { useTokenOnce } from "./AuthPages";

const PROVIDERS = [{ id: "github", name: "GitHub" }, { id: "google", name: "Google" }];

/** Sign-in methods: link GitHub or Google to this account (never silently, ADR-0002). */
export function AccountPage() {
  const { t, tx } = useI18n();
  const { me } = useSession();
  const [params] = useSearchParams();
  const error = params.get("error");
  return (
    <div className="page">
      <h1>{t("account.title")}</h1>
      {error ? <Notice kind="error">{tx(`errors.${error}`)}</Notice> : null}
      <SettingsSection title={t("account.sectionProfile")} hint={t("account.sectionProfileHint")}>
        <Card>
          <p><strong>{me?.displayName}</strong></p>
          <p className="muted">{me?.email}</p>
        </Card>
      </SettingsSection>
      <SettingsSection title={t("account.providers")} hint={t("account.providersHint")}>
        <Card>
          <ul className="plain">
            <li className="spread"><span>{t("account.password")}</span><span className="muted">{me?.hasPassword ? t("account.hasPassword") : t("account.noPassword")}</span></li>
            {PROVIDERS.map((p) => (
              <li key={p.id} className="spread">
                <span>{p.name}</span>
                {me?.providers.includes(p.id)
                  ? <span className="badge ok">{t("account.linked")}</span>
                  : <a className="btn btn-ghost" href={providerUrl(p.id, "link")}>{t("account.link", { provider: p.name })}</a>}
              </li>
            ))}
          </ul>
        </Card>
      </SettingsSection>
      <SettingsSection title={t("account.mfa")} hint={t("account.mfaHint")}>
        <MfaCard titled={false} />
      </SettingsSection>
      <SettingsSection title={t("approvals.title")} hint={t("approvals.sectionHint")}>
        <ApprovalKeysCard titled={false} />
      </SettingsSection>
      <AccountDataCards />
    </div>
  );
}

const accept = (token: string) => api.acceptInvitation(token);

/** /invitations/accept?token=: joins, then shows the workspace. */
export function AcceptInvitationPage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const { refresh } = useSession();
  const { state, error } = useTokenOnce(async (token) => {
    await accept(token);
    await refresh();
  });
  if (state === "done") return <Navigate to="/" replace />;
  return (
    <div className="page narrow">
      {state === "working" ? <Notice>{t("invitation.accepting")}</Notice> : <Notice kind="error">{errorText(error)}</Notice>}
    </div>
  );
}

/** How to install and connect the agent. The commands carry this site's own address. */
export function DownloadPage() {
  const { t } = useI18n();
  const origin = window.location.origin;
  // The download host itself when the server has one (it says so in /version); until then, or when it has none, this site's /downloads, which redirects there.
  const [host, setHost] = useState("");
  useEffect(() => {
    let live = true;
    api.version().then((v) => { if (live && v.downloadsUrl) setHost(v.downloadsUrl); }).catch(() => undefined);
    return () => { live = false; };
  }, []);
  const downloads = host || `${origin}/downloads`;
  const hasPlugin = config.pluginMarketplace !== "" || config.pluginFromServer;
  const files = [
    { os: t("download.macos"), file: "aipod-macos-arm64.zip", label: "Apple silicon" },
    { os: t("download.macos"), file: "aipod-macos-x64.zip", label: "Intel" },
    { os: t("download.windows"), file: "aipod-windows-x64.zip", label: "x64" },
    { os: t("download.windows"), file: "aipod-windows-arm64.zip", label: "ARM64" },
    { os: t("download.linux"), file: "aipod-linux-x64.zip", label: "x64" },
    { os: t("download.linux"), file: "aipod-linux-arm64.zip", label: "ARM64" },
  ];
  return (
    <div className="page">
      <header className="page-head"><div><h1>{t("download.title")}</h1></div></header>
      <p className="lead">{t("download.intro")}</p>
      <SettingsSection title={t("download.installerTitle")} hint={t("download.installerHint")}>
        <Card>
          <div className="stack">
            <p>{t("download.installerIntro")}</p>
            <div className="stack-tight">
              <p className="muted">{t("download.installerUnix")}</p>
              <CopyText text={`curl -fsSL ${downloads}/aipod-install.sh | sh -s -- --server ${origin} --from ${downloads}`} />
            </div>
            <div className="stack-tight">
              <p className="muted">{t("download.installerWindows")}</p>
              <CopyText text={`irm ${downloads}/aipod-install.ps1 -OutFile "$env:TEMP\\aipod-install.ps1"; powershell -ExecutionPolicy Bypass -File "$env:TEMP\\aipod-install.ps1" -Server ${origin} -From ${downloads}`} />
            </div>
          </div>
        </Card>
      </SettingsSection>
      {hasPlugin && (
        <SettingsSection title={t("download.pluginTitle")} hint={t("download.pluginHint")}>
          <Card>
            <div className="stack">
              <p>{t("download.pluginIntro")}</p>
              <ol className="steps">
                <li>
                  <p>{t("download.plugin1")}</p>
                  {config.pluginMarketplace
                    ? <CopyText text={`claude plugin install aipod --marketplace ${config.pluginMarketplace}`} />
                    : <><CopyText text={`claude plugin marketplace add ${downloads}/aipod-marketplace.json`} /><CopyText text="claude plugin install aipod@aipod-releases" /></>}
                  <CopyText text={origin} />
                </li>
                <li>
                  <p>{t("download.plugin2")}</p>
                  <CopyText text="/aipod:connect" />
                </li>
              </ol>
            </div>
          </Card>
        </SettingsSection>
      )}
      <SettingsSection title={t("installOptions.title")} hint={t("installOptions.hint")}>
        <InstallOptions server={origin} />
      </SettingsSection>
      <SettingsSection title={t("download.manualTitle")} hint={t("download.manualHint")}>
        <Card>
          <ol className="steps">
            <li>
              <p>{t("download.step1")}</p>
              <ul className="downloads">
                {files.map((f) => <li key={f.file}><a className="btn btn-ghost" href={`${host || "/downloads"}/${f.file}`}>{f.os} · {f.label}</a></li>)}
              </ul>
            </li>
            <li><p>{t("download.step2")}</p><CopyText text={`aipod login --server ${origin}`} /></li>
            <li><p>{t("download.step3")}</p><CopyText text="aipod install" /></li>
          </ol>
        </Card>
      </SettingsSection>
      <p className="muted"><Link to="/">{t("notFound.home")}</Link></p>
    </div>
  );
}
