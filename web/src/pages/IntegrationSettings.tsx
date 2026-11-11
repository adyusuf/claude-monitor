import { useState } from "react";
import { api } from "../api/endpoints";
import type { ConnectionView } from "../api/typesIntegrations";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";

/** The rest of one connection, in its drawer: whether it needs a new sign-in, live updates, the last problem, the person's own Atlassian account and the invite switch. */
export function IntegrationSettings({ connection: c, admin, onChange }: { connection: ConnectionView; admin: boolean; onChange: () => void }) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [error, setError] = useState<string | null>(null);

  const act = async (work: () => Promise<unknown>) => {
    setError(null);
    try {
      await work();
      onChange();
    } catch (e) {
      setError(errorText(e));
    }
  };
  const link = () => act(async () => window.location.assign((await api.linkMyAccount(c.id)).authorizeUrl));
  const accountText = !c.myAccountLinked ? t("integrations.myAccountNone") : c.myAccountStatus === "reauthorize" ? t("integrations.myAccountAgain") : t("integrations.myAccountLinked");

  return (
    <div className="stack">
      {error ? <Notice kind="error">{error}</Notice> : null}
      {c.status === "reauthorize" ? <Notice kind="error">{t("integrations.needsSignIn")}</Notice> : null}
      <p className="muted">
        {t("integrations.connectedBy", { name: c.connectorName })} · {t("integrations.projects", { n: c.projectCount })} ·{" "}
        {c.lastPassAt ? t("integrations.lastPass", { when: dateTime(c.lastPassAt) }) : t("integrations.neverChecked")}
      </p>
      <p className="muted">
        {c.webhookActive && c.webhookExpiresAt ? t("integrations.webhookOn", { date: dateTime(c.webhookExpiresAt) }) : t("integrations.webhookOff")}
        {c.lastError ? <> · {t("integrations.lastError", { error: tx(`errors.${c.lastError}`) })}</> : null}
      </p>
      <p>
        <strong>{t("integrations.myAccount")}:</strong> {accountText}{" "}
        <Button variant="ghost" onClick={() => void link()}>{c.myAccountLinked ? t("integrations.relink") : t("integrations.linkAccount")}</Button>
      </p>
      {admin ? (
        <label className="check">
          <input type="checkbox" checked={c.autoInvite} onChange={(e) => void act(() => api.setIntegration(c.id, e.target.checked))} />
          <span>{t("integrations.autoInvite")}<br /><span className="muted">{t("integrations.autoInviteHint")}</span></span>
        </label>
      ) : null}
    </div>
  );
}
