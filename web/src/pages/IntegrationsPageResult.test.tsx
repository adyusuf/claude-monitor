import { screen } from "@testing-library/react";
import { renderToString } from "react-dom/server";
import { StaticRouter, useLocation } from "react-router";
import { describe, expect, it } from "vitest";
import { SessionProvider } from "../auth/session";
import { I18nProvider } from "../i18n";
import { renderAt } from "../test/helpers";
import { serveIntegrations as serve, siteHeading } from "../test/integrations-fixtures";
import { IntegrationsPage } from "./IntegrationsPage";

/** Shows the address's query string, so a test can see that the result code was taken out of it. */
function Where() {
  return <p data-testid="where">{useLocation().search}</p>;
}
const route = [{ path: "/w/:ws/integrations", element: <><IntegrationsPage /><Where /></> }];

/** What the page puts on screen on its first render for an address (no effects run, so nothing has been cleared yet). */
const firstRender = (query: string) => renderToString(
  <I18nProvider initial="en"><StaticRouter location={`/w/w1/integrations${query}`}><SessionProvider><IntegrationsPage /></SessionProvider></StaticRouter></I18nProvider>,
);

describe("the integrations page: the result of the Atlassian round trip", () => {
  it("takes the result code out of the address once the page is up", async () => {
    serve();
    renderAt("/w/w1/integrations?result=denied", route);
    await screen.findByRole("heading", siteHeading("acme.atlassian.net"));
    expect(screen.getByTestId("where").textContent).toBe("");
  });

  it("leaves an address without a result code alone", async () => {
    serve();
    renderAt("/w/w1/integrations?tab=x", route);
    await screen.findByRole("heading", siteHeading("acme.atlassian.net"));
    expect(screen.getByTestId("where").textContent).toBe("?tab=x");
  });

  it("shows ok as a success notice", () => {
    const html = firstRender("?result=ok");
    expect(html).toContain("notice-success");
    expect(html).toContain("Done.");
  });

  it.each([
    ["denied", "Atlassian did not give access, nothing was connected."],
    ["invalid_state", "That sign-in has expired. Start again."],
    ["site_not_found", "Your Atlassian login cannot reach that Jira address."],
  ])("shows %s as an error notice with its own text", (code, text) => {
    const html = firstRender(`?result=${code}`);
    expect(html).toContain("notice-error");
    expect(html).toContain(text);
  });

  it("shows any other code as errors.<code>, and an unknown one as the key itself", () => {
    expect(firstRender("?result=jira_account_required")).toContain("Link your own Atlassian account first.");
    const unknown = firstRender("?result=zzz_unknown");
    expect(unknown).toContain("notice-error");
    expect(unknown).toContain("errors.zzz_unknown");
  });

  it("shows no result notice without a code", () => {
    const html = firstRender("");
    expect(html).not.toContain("notice-success");
    expect(html).not.toContain("notice-error");
  });

  // The notice is read once; the effect then empties the address, and the notice must stay for the person to read.
  it("keeps the result notice on screen after the address was cleaned", async () => {
    serve();
    renderAt("/w/w1/integrations?result=ok", route);
    await screen.findByRole("heading", siteHeading("acme.atlassian.net"));
    expect(screen.getByRole("status", { name: "" })).toHaveTextContent("Done.");
    expect(screen.getByTestId("where").textContent).toBe("");
  });
});
