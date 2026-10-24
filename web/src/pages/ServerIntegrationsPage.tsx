import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import { ApiError } from "../api/client";
import type { ServerIntegrationsView } from "../api/typesServerSettings";
import { Empty, Notice, Spinner } from "../components/ui";
import { useErrorText, useI18n } from "../i18n";
import { ServerIntegrationCard } from "./ServerIntegrationCard";

const NOT_FOUND = 404;

/**
 * The server-wide integration settings (ADR-0017): e-mail, the sign-in providers and Jira, one card each. Only a server admin
 * gets the view; anyone else gets 404 from the API and this page says it is not theirs.
 */
export function ServerIntegrationsPage() {
  const { t } = useI18n();
  const errorText = useErrorText();
  const [view, setView] = useState<ServerIntegrationsView | null>(null);
  const [denied, setDenied] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.serverIntegrations().then((v) => {
      if (live) setView(v);
    }).catch((e: unknown) => {
      if (!live) return;
      if (e instanceof ApiError && e.status === NOT_FOUND) setDenied(true);
      else setError(errorText(e));
    });
    return () => {
      live = false;
    };
    // errorText is a fresh function each render
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (denied) return <div className="page"><Empty title={t("serverSettings.noAccess")} /></div>;

  return (
    <div className="page">
      <h1>{t("serverSettings.title")}</h1>
      <p className="muted">{t("serverSettings.intro")}</p>
      {error ? <Notice kind="error">{error}</Notice> : !view ? <Spinner /> : (
        <>
          {!view.canSave ? <Notice>{t("serverSettings.cannotSave")}</Notice> : null}
          {view.integrations.map((i) => <ServerIntegrationCard key={i.id} integration={i} canSave={view.canSave} onSaved={setView} />)}
        </>
      )}
    </div>
  );
}
