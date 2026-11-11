import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { useLocation } from "react-router";
import { renderAt } from "../test/helpers";
import { connection, findSite, integrationsView, serveIntegrations as serve } from "../test/integrations-fixtures";
import { meAs } from "../test/projects-fixtures";
import { IntegrationsPage } from "./IntegrationsPage";

/** Shows the address's query string, so a test can see that the result code was taken out of it. */
function Where() {
  return <p data-testid="where">{useLocation().search}</p>;
}
const route = [{ path: "/w/:ws/integrations", element: <><IntegrationsPage /><Where /></> }];
const site = findSite;
/** Presses a link of a connection's row (Spaces, People from Jira, Recent checks, Settings): its drawer opens. */
const openDrawer = async (name: string) => {
  await userEvent.click(screen.getByRole("button", { name }));
  return within(await screen.findByRole("dialog"));
};
const closeDrawer = async () => userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Close" }));
const open = (query = "") => renderAt(`/w/w1/integrations${query}`, route);

describe("the integrations page: what it shows", () => {
  it("shows the spinner until the connections arrive, then the connection and the connect card for an admin", async () => {
    serve();
    open();
    expect(screen.getByRole("status", { name: "Loading…" })).toBeInTheDocument();
    expect(await site("acme.atlassian.net")).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: "Loading…" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "Integrations" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Connect Jira" })).toBeInTheDocument();
  });

  it("shows the unavailable notice and neither the connect form nor any connection when Jira is not set up", async () => {
    serve({ "GET /workspaces/w1/integrations": { body: integrationsView([connection()], { available: false }) } });
    open();
    expect(await screen.findByText(/Jira is not set up on this server yet/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Connect Jira" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Jira address")).not.toBeInTheDocument();
    expect(screen.queryByText("acme.atlassian.net")).not.toBeInTheDocument();
  });

  it("shows one row per connection, each with its own links", async () => {
    serve({
      "GET /workspaces/w1/integrations": { body: integrationsView([connection(), connection({ id: "c2", siteHost: "beta.atlassian.net" })]) },
      "GET /integrations/c2/runs": { body: [] },
      "GET /integrations/c2/people": { body: [] },
    });
    open();
    expect(await site("acme.atlassian.net")).toBeInTheDocument();
    expect(screen.getByText("beta.atlassian.net")).toBeInTheDocument();
    expect(screen.getAllByRole("row")).toHaveLength(3); // the header and one row each
    expect(screen.getAllByRole("button", { name: "Spaces" })).toHaveLength(2);
  });

  it.each(["admin", "owner"] as const)("gives a workspace %s the connect button, the import button and the admin actions", async (role) => {
    serve({ "GET /me": { body: meAs(role) }, "GET /integrations/c1/spaces": { body: [{ key: "SHOP", name: "Shop", description: null, imported: false, projectId: null }] } });
    open();
    await site("acme.atlassian.net");
    expect(screen.queryByLabelText("Jira address")).not.toBeInTheDocument(); // the connect form is not open on the page
    await userEvent.click(screen.getByRole("button", { name: "Connect Jira" }));
    expect(within(await screen.findByRole("dialog")).getByLabelText("Jira address")).toBeInTheDocument();
    await closeDrawer();
    expect(screen.getByRole("button", { name: "Check now" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Disconnect" })).toBeInTheDocument();
    expect((await openDrawer("People from Jira")).getByText(/A person with a visible e-mail address/)).toBeInTheDocument();
    await closeDrawer();
    expect((await openDrawer("Settings")).getByRole("checkbox", { name: /Invite Jira people/ })).toBeInTheDocument();
  });

  it.each(["member", "viewer"] as const)("shows a %s the connections but no connect card, no people and no actions", async (role) => {
    serve({ "GET /me": { body: meAs(role) }, "GET /integrations/c1/spaces": { body: [{ key: "SHOP", name: "Shop", description: null, imported: false, projectId: null }] } });
    open();
    expect(await site("acme.atlassian.net")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Connect Jira" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Check now" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Disconnect" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "People from Jira" })).not.toBeInTheDocument();
    expect((await openDrawer("Settings")).queryByRole("checkbox", { name: /Invite Jira people/ })).not.toBeInTheDocument();
    await closeDrawer();
    expect((await openDrawer("Spaces")).getByRole("button", { name: "Show Jira spaces" })).toBeInTheDocument(); // reading is allowed
  });

  it("treats a workspace the person has no role in as no admin (fail closed)", async () => {
    serve({ "GET /workspaces/w3/integrations": { body: integrationsView() } });
    renderAt("/w/w3/integrations", route);
    expect(await site("acme.atlassian.net")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Connect Jira" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Disconnect" })).not.toBeInTheDocument();
  });

  it("shows the translated problem when the connections cannot be loaded", async () => {
    serve({ "GET /workspaces/w1/integrations": { status: 403, body: { title: "forbidden" } } });
    open();
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
    expect(screen.queryByRole("button", { name: "Connect Jira" })).not.toBeInTheDocument();
  });

  it("shows the server-failure text when the API answers 500", async () => {
    serve({ "GET /workspaces/w1/integrations": { status: 500 } });
    open();
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
  });
});
