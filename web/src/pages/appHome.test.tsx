import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { App } from "../App";
import { ME, mockApi, renderAt } from "../test/helpers";

describe("the home page", () => {
  it("sends a user who belongs to no workspace to create the first one", async () => {
    mockApi({ "GET /me": { body: { ...ME, workspaces: [] } } });
    renderAt("/", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { name: "New workspace" })).toBeInTheDocument();
  });

  it("sends a user with workspaces to the projects of the first one, not to the creation page", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": { body: { items: [], next: null } } });
    renderAt("/", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { name: "Projects" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "New workspace" })).toBeNull();
  });
});
