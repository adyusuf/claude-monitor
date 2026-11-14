import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { renderAt } from "../test/helpers";
import { connection, findSite, integrationsView, serveIntegrations as serve, syncRun } from "../test/integrations-fixtures";
import { IntegrationsPage } from "./IntegrationsPage";

const route = [{ path: "/w/:ws/integrations", element: <IntegrationsPage /> }];
const reads = (calls: { method: string; path: string }[]) => calls.filter((c) => c.method === "GET" && c.path === "/workspaces/w1/integrations");

describe("the connections table", () => {
  it("opens a connection's recent checks in a drawer titled with its site", async () => {
    serve({ "GET /integrations/c1/runs": { body: [syncRun()] } });
    renderAt("/w/w1/integrations", route);
    await findSite("acme.atlassian.net");
    await userEvent.click(screen.getByRole("button", { name: en.integrations.runsTitle }));
    const drawer = within(await screen.findByRole("dialog", { name: `acme.atlassian.net · ${en.integrations.runsTitle}` }));
    expect(await drawer.findByText("Done")).toBeInTheDocument();
  });

  it("opens the checks of the connection that was pressed, not of another one", async () => {
    serve({
      "GET /workspaces/w1/integrations": { body: integrationsView([connection(), connection({ id: "c2", siteHost: "beta.atlassian.net" })]) },
      "GET /integrations/c1/runs": { body: [syncRun({ created: 3 })] },
      "GET /integrations/c2/runs": { body: [syncRun({ id: "r2", created: 9 })] },
      "GET /integrations/c2/people": { body: [] },
    });
    renderAt("/w/w1/integrations", route);
    await findSite("beta.atlassian.net");
    await userEvent.click(screen.getAllByRole("button", { name: en.integrations.runsTitle })[1]!);
    const drawer = within(await screen.findByRole("dialog", { name: `beta.atlassian.net · ${en.integrations.runsTitle}` }));
    expect(await drawer.findByText(/9 new/)).toBeInTheDocument();
    expect(drawer.queryByText(/3 new/)).toBeNull();
  });

  it("closes the drawer when the person closes it", async () => {
    serve();
    renderAt("/w/w1/integrations", route);
    await findSite("acme.atlassian.net");
    await userEvent.click(screen.getByRole("button", { name: en.integrations.runsTitle }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: en.common.close }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("reads the connections again after a connection changed, so the page shows the new state", async () => {
    const calls = serve({ "POST /integrations/c1/sync": { status: 204 } });
    renderAt("/w/w1/integrations", route);
    await findSite("acme.atlassian.net");
    expect(reads(calls)).toHaveLength(1);
    await userEvent.click(screen.getByRole("button", { name: en.integrations.syncNow }));
    await waitFor(() => expect(reads(calls)).toHaveLength(2));
  });
});
