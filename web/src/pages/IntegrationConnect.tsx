import { useState, type FormEvent } from "react";
import { api } from "../api/endpoints";
import { ApiError } from "../api/client";
import { Button, Card, Field, Notice } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";

/** Starts the Atlassian sign-in for a Jira address: the API makes the address, the browser goes there and comes back to this page. */
export function IntegrationConnect({ ws }: { ws: string }) {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [subdomain, setSubdomain] = useState("");
  const [code, setCode] = useState("");
  const [needsCode, setNeedsCode] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { authorizeUrl } = await api.connectJira(ws, subdomain.trim(), code.trim() || undefined);
      window.location.assign(authorizeUrl);
    } catch (err) {
      setNeedsCode(err instanceof ApiError && err.code === "reauth_required");
      setError(errorText(err));
      setBusy(false);
    }
  };

  return (
    <Card title={t("integrations.connectTitle")}>
      <form onSubmit={submit} className="stack">
        <Field label={t("integrations.subdomain")} hint={t("integrations.subdomainHint")} value={subdomain} placeholder="acme"
          onChange={(e) => setSubdomain(e.target.value)} autoComplete="off" required />
        {needsCode ? <Field label={t("integrations.twoStepCode")} value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" /> : null}
        {error ? <Notice kind="error">{error}</Notice> : null}
        <p className="muted">{t("integrations.connectHint")}</p>
        <div><Button type="submit" busy={busy}>{t("integrations.connect")}</Button></div>
      </form>
    </Card>
  );
}
