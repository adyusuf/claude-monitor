import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderAt } from "../test/helpers";
import { serveIntegrations as serve, syncRun } from "../test/integrations-fixtures";
import { IntegrationChecks } from "./IntegrationChecks";

const open = () => renderAt("/", [{ path: "/", element: <IntegrationChecks id="c1" /> }]);

describe("the recent checks of a connection", () => {
  it("shows the recent checks from the API", async () => {
    serve({ "GET /integrations/c1/runs": { body: [syncRun()] } });
    open();
    expect(await screen.findByText("Done")).toHaveClass("badge", "ok");
    expect(screen.getByText(/3 new · 4 updated · 1 archived · 2 people/)).toBeInTheDocument();
  });

  it("shows the empty text when there are none", async () => {
    serve({ "GET /integrations/c1/runs": { body: [] } });
    open();
    expect(await screen.findByText("Nothing has run yet.")).toBeInTheDocument();
  });

  it("shows the empty text when they cannot be loaded", async () => {
    serve({ "GET /integrations/c1/runs": { status: 500 } });
    open();
    expect(await screen.findByText("Nothing has run yet.")).toBeInTheDocument();
  });
});
