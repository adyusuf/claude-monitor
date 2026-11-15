import { useState, type FormEvent } from "react";
import { api } from "../api/endpoints";
import { useSession } from "../auth/session";
import { Button, Card, Field, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { useCodeGate } from "./useCodeGate";

/**
 * Starts the Atlassian sign-in for a Jira address: the API makes the address, the browser goes there and comes back to this page. `bare` leaves
 * out the card (the drawer has its own title). Connecting needs a recent sign-in: a person with two-step sign-in gets the code field, anyone
 * else is told to sign in again (a code can never pass for them).
 */
export function IntegrationConnect({ ws, bare }: { ws: string; bare?: boolean }) {
  const { t } = useI18n();
  const { me } = useSession();
  const gate = useCodeGate("reauth_required", "remote.codeHintReauth");
  const [subdomain, setSubdomain] = useState("");
  const twoStep = me?.mfaEnabled === true;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    await gate.attempt(async (code) => {
      const { authorizeUrl } = await api.connectJira(ws, subdomain.trim(), code);
      window.location.assign(authorizeUrl);
    });
  };

  const asked = gate.field !== null;
  const form = (
      <form onSubmit={submit} className="stack">
        <Field label={t("integrations.subdomain")} hint={t("integrations.subdomainHint")} value={subdomain} placeholder="acme"
          onChange={(e) => setSubdomain(e.target.value)} autoComplete="off" required />
        {asked && twoStep ? gate.field : null}
        {asked && !twoStep ? <Notice>{t("integrations.reauthSignIn")}</Notice> : null}
        {gate.error ? <Notice kind="error">{gate.error}</Notice> : null}
        <p className="muted">{t("integrations.connectHint")}</p>
        <div><Button type="submit" busy={gate.busy} disabled={twoStep && !gate.ready}>{t("integrations.connect")}</Button></div>
      </form>
  );
  return bare ? form : <Card title={t("integrations.connectTitle")}>{form}</Card>;
}
