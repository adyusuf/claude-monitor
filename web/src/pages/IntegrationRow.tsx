import { useState } from "react";
import { api } from "../api/endpoints";
import type { ConnectionView } from "../api/typesIntegrations";
import { useSession } from "../auth/session";
import { Button, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { dateTime } from "../lib/format";
import { useCodeGate } from "./useCodeGate";

export type IntegrationPanel = "spaces" | "people" | "checks" | "settings";

/**
 * One Jira connection as a table row: how it stands, links that open its spaces, people, checks and settings in a drawer, and (admins) check now
 * and disconnect. Check now is left out while Jira is not available on the server (canCheck false); disconnect never is. Disconnecting needs a
 * recent sign-in (F-081): a person with two-step sign-in gets the code field once the API asks, anyone else is told to sign in again.
 */
export function IntegrationRow({ connection: c, admin, canCheck = true, onOpen, onChange, onError }: {
  connection: ConnectionView; admin: boolean; canCheck?: boolean; onOpen: (panel: IntegrationPanel) => void; onChange: () => void; onError: (text: string) => void;
}) {
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const [busy, setBusy] = useState(false);
  const { me } = useSession();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const twoStep = me?.mfaEnabled === true;
  const asked = gate.field !== null;

  const disconnect = async () => {
    // The question is asked once; a second click after the code was asked for sends the code.
    if (!asked && !window.confirm(t("integrations.disconnectConfirm", { site: c.siteHost }))) return;
    if (await gate.attempt((code) => api.disconnectIntegration(c.id, code))) onChange();
  };

  const act = async (work: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await work();
      onChange();
    } catch (e) {
      onError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <tr>
      <td>
        <strong>{c.siteHost}</strong> <span className={c.status === "active" ? "badge ok" : "badge warn"}>{t(`integrations.status.${c.status}`)}</span>
        {c.status === "reauthorize" ? <div className="muted small">{t("integrations.needsSignIn")}</div> : null}
        {c.lastError ? <div className="muted small">{t("integrations.lastError", { error: tx(`errors.${c.lastError}`) })}</div> : null}
      </td>
      <td>{c.connectorName}</td>
      <td className="num">{c.projectCount}</td>
      <td className="muted small">{c.lastPassAt ? dateTime(c.lastPassAt) : t("integrations.neverChecked")}</td>
      <td>
        <span className="row-actions">
          <button type="button" className="link" onClick={() => onOpen("spaces")}>{t("integrations.spacesTitle")}</button>
          {admin ? <button type="button" className="link" onClick={() => onOpen("people")}>{t("integrations.peopleTitle")}</button> : null}
          <button type="button" className="link" onClick={() => onOpen("checks")}>{t("integrations.runsTitle")}</button>
          <button type="button" className="link" onClick={() => onOpen("settings")}>{t("views.integrations.openSettings")}</button>
        </span>
      </td>
      <td className="right">
        {admin ? (
          <span className="row-actions">
            {canCheck ? (
              <Button variant="ghost" busy={busy} onClick={() => void act(async () => { await api.syncIntegration(c.id); })}>{busy ? t("integrations.syncing") : t("integrations.syncNow")}</Button>
            ) : null}
            <Button variant="danger" busy={gate.busy} disabled={busy || (asked && twoStep && !gate.ready)} onClick={() => void disconnect()}>
              {t("integrations.disconnect")}
            </Button>
          </span>
        ) : null}
        {admin && asked && twoStep ? gate.field : null}
        {admin && asked && !twoStep ? <Notice>{t("integrations.disconnectSignIn")}</Notice> : null}
        {admin && gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
      </td>
    </tr>
  );
}
