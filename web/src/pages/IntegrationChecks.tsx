import { useEffect, useState } from "react";
import { api } from "../api/endpoints";
import type { SyncRunView } from "../api/typesIntegrations";
import { Spinner } from "../components/ui";
import { IntegrationRuns } from "./IntegrationRuns";

/** The recent checks of one connection, read when its drawer opens. */
export function IntegrationChecks({ id }: { id: string }) {
  const [runs, setRuns] = useState<SyncRunView[] | null>(null);
  useEffect(() => {
    let current = true;
    api.integrationRuns(id).then((r) => current && setRuns(r)).catch(() => current && setRuns([]));
    return () => {
      current = false;
    };
  }, [id]);
  return runs === null ? <Spinner /> : <IntegrationRuns runs={runs} />;
}
