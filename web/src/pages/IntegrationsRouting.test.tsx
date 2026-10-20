import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App } from "../App";
import { Layout } from "../components/Layout";
import { dictionaries } from "../i18n";
import { ME, mockApi, renderAt } from "../test/helpers";
import { serveIntegrations } from "../test/integrations-fixtures";

describe("integrations in the frame and the routes", () => {
  it("puts Integrations in the sidebar of the workspace, linking to its page", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    expect(await screen.findByRole("link", { name: /Integrations/ })).toHaveAttribute("href", "/w/w1/integrations");
  });

  it.each(["tr", "de"] as const)("words the sidebar entry in %s", async (language) => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    await userEvent.selectOptions(await screen.findByLabelText("Language"), language);
    expect(await screen.findByRole("link", { name: new RegExp(dictionaries[language].nav.integrations) })).toHaveAttribute("href", "/w/w1/integrations");
  });

  it("opens the page at /w/:ws/integrations inside the frame", async () => {
    serveIntegrations();
    renderAt("/w/w1/integrations", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { level: 1, name: "Integrations" })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { level: 2, name: /acme\.atlassian\.net/ })).toBeInTheDocument();
  });
});
