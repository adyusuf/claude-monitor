import { useCallback, useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { ConnectionView, SyncRunView } from "../api/typesIntegrations";
import { Button, Card, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { IntegrationPeople } from "./IntegrationPeople";
import { IntegrationRuns } from "./IntegrationRuns";
import { IntegrationSpaces } from "./IntegrationSpaces";

/** One connection: how it stands, the switches and actions, then the spaces, the people and the last checks. */
export function IntegrationCard({ connection: c, ws, admin, onChange }: { connection: ConnectionView; ws: string; admin: boolean; onChange: () => void }) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [runs, setRuns] = useState<SyncRunView[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadRuns = useCallback(() => api.integrationRuns(c.id).then(setRuns).catch(() => setRuns([])), [c.id]);
  useEffect(() => {
    void loadRuns();
  }, [loadRuns]);

  const act = async (work: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
      onChange();
      await loadRuns();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const link = () => act(async () => window.location.assign((await api.linkMyAccount(c.id)).authorizeUrl));
  const accountText = !c.myAccountLinked ? t("integrations.myAccountNone") : c.myAccountStatus === "reauthorize" ? t("integrations.myAccountAgain") : t("integrations.myAccountLinked");

  return (
    <Card title={<>{c.siteHost} <span className={c.status === "active" ? "badge ok" : "badge warn"}>{t(`integrations.status.${c.status}`)}</span></>}
      actions={admin ? (
        <span className="row-actions">
          <Button variant="ghost" busy={busy} onClick={() => void act(async () => { await api.syncIntegration(c.id); })}>{busy ? t("integrations.syncing") : t("integrations.syncNow")}</Button>
          <Button variant="danger" onClick={() => { if (window.confirm(t("integrations.disconnectConfirm", { site: c.siteHost }))) void act(() => api.disconnectIntegration(c.id)); }}>
            {t("integrations.disconnect")}
          </Button>
        </span>
      ) : undefined}>
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
      <h3>{t("integrations.spacesTitle")}</h3>
      <IntegrationSpaces id={c.id} ws={ws} canImport={admin} onImported={onChange} />
      {admin ? <><h3>{t("integrations.peopleTitle")}</h3><IntegrationPeople id={c.id} ws={ws} /></> : null}
      <h3>{t("integrations.runsTitle")}</h3>
      <IntegrationRuns runs={runs} />
    </Card>
  );
}
