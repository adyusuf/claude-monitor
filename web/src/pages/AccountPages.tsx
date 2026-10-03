import { Link, Navigate, useSearchParams } from "react-router";
import { api, providerUrl } from "../api/endpoints";
import { useSession } from "../auth/session";
import { Card, CopyText, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { useTokenOnce } from "./AuthPages";

const PROVIDERS = [{ id: "github", name: "GitHub" }, { id: "google", name: "Google" }];

/** Sign-in methods: link GitHub or Google to this account (never silently, ADR-0002). */
export function AccountPage() {
  const { t, tx } = useI18n();
  const { me } = useSession();
  const [params] = useSearchParams();
  const error = params.get("error");
  return (
    <div className="page narrow">
      <h1>{t("account.title")}</h1>
      {error ? <Notice kind="error">{tx(`errors.${error}`)}</Notice> : null}
      <Card>
        <p><strong>{me?.displayName}</strong></p>
        <p className="muted">{me?.email}</p>
      </Card>
      <Card title={t("account.providers")}>
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
  const files = [
    { os: t("download.macos"), file: "cm-agent-macos-arm64.zip", label: "Apple silicon" },
    { os: t("download.macos"), file: "cm-agent-macos-x64.zip", label: "Intel" },
    { os: t("download.windows"), file: "cm-agent-windows-x64.zip", label: "x64" },
    { os: t("download.windows"), file: "cm-agent-windows-arm64.zip", label: "ARM64" },
  ];
  return (
    <div className="page narrow">
      <h1>{t("download.title")}</h1>
      <p className="lead">{t("download.intro")}</p>
      <Card title={`1. ${t("download.step1")}`}>
        <ul className="downloads">
          {files.map((f) => <li key={f.file}><a className="btn btn-ghost" href={`/downloads/${f.file}`}>{f.os} · {f.label}</a></li>)}
        </ul>
      </Card>
      <Card title={`2. ${t("download.step2")}`}><CopyText text={`cm-agent login --server ${origin}`} /></Card>
      <Card title={`3. ${t("download.step3")}`}><CopyText text="cm-agent install" /></Card>
      <p className="muted"><Link to="/">{t("notFound.home")}</Link></p>
    </div>
  );
}
