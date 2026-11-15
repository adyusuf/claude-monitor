import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { SyncRunView } from "../api/typesIntegrations";
import { Notice, Spinner } from "../components/ui";
import { useErrorText } from "../i18n";
import { IntegrationRuns } from "./IntegrationRuns";

/** The recent checks of one connection, read when its drawer opens. A failed read shows its problem, never the "nothing has run" text. */
export function IntegrationChecks({ id }: { id: string }) {
  const errorText = useErrorText();
  const [runs, setRuns] = useState<SyncRunView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let current = true;
    api.integrationRuns(id).then((r) => current && setRuns(r)).catch((e) => current && setError(errorText(e)));
    return () => {
      current = false;
    };
    // errorText is a fresh function each render
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);
  if (error) return <Notice kind="error">{error}</Notice>;
  return runs === null ? <Spinner /> : <IntegrationRuns runs={runs} />;
}
