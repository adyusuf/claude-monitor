import { useCallback, useEffect, useState } from "react";
import { useParams, useSearchParams } from "react-router";
import { api } from "../api/endpoints";
import type { IntegrationsView } from "../api/typesIntegrations";
import { useSession } from "../auth/session";
import { Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { IntegrationCard } from "./IntegrationCard";
import { IntegrationConnect } from "./IntegrationConnect";

/** Work tracker connections of the workspace (ADR-0015): connect Jira, then import spaces and sprints from each connection. */
export function IntegrationsPage() {
  const { ws = "" } = useParams();
  const { t, tx } = useI18n();
  const errorText = useErrorText();
  const { me } = useSession();
  const [params, setParams] = useSearchParams();
  const [view, setView] = useState<IntegrationsView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const role = me?.workspaces.find((w) => w.id === ws)?.role;
  const admin = role === "owner" || role === "admin";
  // The result of the Atlassian round trip is read once, shown, and only then taken out of the address.
  const incoming = params.get("result");
  const [result] = useState(incoming); // the round trip is a full page load, so the first value is the one

  const load = useCallback(async () => {
    try {
      setView(await api.integrations(ws));
      setError(null);
    } catch (e) {
      setError(errorText(e));
    }
    // errorText is a fresh function each render
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ws]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (incoming) setParams({}, { replace: true });
  }, [incoming, setParams]);

  return (
    <div className="page">
      <h1>{t("integrations.title")}</h1>
      <p className="muted">{t("integrations.intro")}</p>
      {result ? <Notice kind={result === "ok" ? "success" : "error"}>{result === "ok" ? t("integrations.result.ok") : resultText(result, t, tx)}</Notice> : null}
      {error ? <Notice kind="error">{error}</Notice> : null}
      {!view ? <Spinner /> : !view.available ? <Notice>{t("integrations.unavailable")}</Notice> : (
        <>
          {admin ? <IntegrationConnect ws={ws} /> : null}
          {view.connections.map((c) => <IntegrationCard key={c.id} connection={c} ws={ws} admin={admin} onChange={() => void load()} />)}
        </>
      )}
    </div>
  );
}

const KNOWN_RESULTS = ["denied", "invalid_state", "site_not_found"] as const;

function resultText(code: string, t: ReturnType<typeof useI18n>["t"], tx: ReturnType<typeof useI18n>["tx"]): string {
  const known = KNOWN_RESULTS.find((k) => k === code);
  return known ? t(`integrations.result.${known}`) : tx(`errors.${code}`);
}
