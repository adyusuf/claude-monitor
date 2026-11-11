import { useState, type ReactNode } from "react";
import type { ConnectionView } from "../api/typesIntegrations";
import { Drawer } from "../components/Drawer";
import { Card, Notice } from "../components/ui";
import { useI18n } from "../i18n";
import { IntegrationChecks } from "./IntegrationChecks";
import { IntegrationPeople } from "./IntegrationPeople";
import { IntegrationRow, type IntegrationPanel } from "./IntegrationRow";
import { IntegrationSettings } from "./IntegrationSettings";
import { IntegrationSpaces } from "./IntegrationSpaces";

/**
 * The Jira connections of the workspace, one row each (several sites can be connected; each is imported and checked on its own). The spaces, the
 * people, the recent checks and the settings of a connection each open in a drawer from a link in its row.
 */
export function IntegrationsTable({ connections, ws, admin, onChange }: { connections: ConnectionView[]; ws: string; admin: boolean; onChange: () => void }) {
  const { t } = useI18n();
  const [open, setOpen] = useState<{ id: string; panel: IntegrationPanel } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const current = open ? connections.find((c) => c.id === open.id) : undefined;

  const titles: Record<IntegrationPanel, string> = {
    spaces: t("integrations.spacesTitle"), people: t("integrations.peopleTitle"), checks: t("integrations.runsTitle"), settings: t("views.integrations.openSettings"),
  };
  const body = (c: ConnectionView, panel: IntegrationPanel): ReactNode => {
    switch (panel) {
      case "spaces": return <IntegrationSpaces id={c.id} ws={ws} canImport={admin} onImported={onChange} />;
      case "people": return admin ? <IntegrationPeople id={c.id} ws={ws} /> : null;
      case "checks": return <IntegrationChecks id={c.id} />;
      case "settings": return <IntegrationSettings connection={c} admin={admin} onChange={onChange} />;
      default: return null;
    }
  };

  return (
    <>
      {error ? <Notice kind="error">{error}</Notice> : null}
      <Card>
        {connections.length === 0 ? <p className="muted">{t("views.integrations.none")}</p> : (
          <table className="table">
            <thead>
              <tr>
                <th>{t("views.integrations.colConnection")}</th><th>{t("views.integrations.colConnectedBy")}</th><th>{t("views.integrations.colProjects")}</th>
                <th>{t("views.integrations.colLastCheck")}</th><th>{t("views.integrations.colDetails")}</th><th />
              </tr>
            </thead>
            <tbody>
              {connections.map((c) => (
                <IntegrationRow key={c.id} connection={c} admin={admin} onChange={onChange}
                  onError={setError} onOpen={(panel) => { setError(null); setOpen({ id: c.id, panel }); }} />
              ))}
            </tbody>
          </table>
        )}
      </Card>
      {open && current ? <Drawer title={`${current.siteHost} · ${titles[open.panel]}`} onClose={() => setOpen(null)}>{body(current, open.panel)}</Drawer> : null}
    </>
  );
}
