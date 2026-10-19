import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { App } from "../App";
import { Layout } from "../components/Layout";
import { ME, mockApi, renderAt } from "../test/helpers";
import { application, project } from "../test/projects-fixtures";

describe("projects in the frame and the routes", () => {
  it("puts Projects first in the sidebar, before Sessions", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    const projects = await screen.findByRole("link", { name: /Projects/ });
    expect(projects).toHaveAttribute("href", "/w/w1/projects");
    const links = screen.getAllByRole("link").map((l) => l.getAttribute("href"));
    expect(links.indexOf("/w/w1/projects")).toBeLessThan(links.indexOf("/w/w1/sessions"));
    expect(links.indexOf("/w/w1/projects")).toBe(0);
  });

  it("shows the link in Turkish too", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }]);
    await userEvent.selectOptions(await screen.findByLabelText("Language"), "tr");
    expect(await screen.findByRole("link", { name: /Projeler/ })).toHaveAttribute("href", "/w/w1/projects");
  });

  it("opens the projects of another workspace from the switcher", async () => {
    mockApi({ "GET /me": { body: ME } });
    renderAt("/w/w1/x", [{ path: "/w/:ws/*", element: <Layout /> }, { path: "/w/:ws/projects", element: <div>projects of the other</div> }]);
    await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Workspace" }), "w2");
    expect(await screen.findByText("projects of the other")).toBeInTheDocument();
  });

  it("opens the projects of the first workspace from the start page", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/projects": { body: { items: [], next: null } } });
    renderAt("/", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { level: 1, name: "Projects" })).toBeInTheDocument();
  });

  it("sends a user with no workspace to create one", async () => {
    mockApi({ "GET /me": { body: { ...ME, workspaces: [] } } });
    renderAt("/", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { name: "New workspace" })).toBeInTheDocument();
  });

  it("routes the project and the application pages", async () => {
    mockApi({
      "GET /me": { body: ME }, "GET /projects/p1": { body: project() }, "GET /workspaces/w1/members": { body: [] }, "GET /projects/p1/members": { body: [] },
      "GET /projects/p1/applications": { body: { items: [], next: null } }, "GET /applications/a1": { body: application() },
      "GET /applications/a1/components": { body: [] }, "GET /applications/a1/releases": { body: [] },
    });
    renderAt("/w/w1/projects/p1", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { level: 1, name: /Checkout/ })).toBeInTheDocument();
    renderAt("/w/w1/projects/p1/applications/a1", [{ path: "*", element: <App /> }]);
    expect(await screen.findByRole("heading", { level: 1, name: /Shop site/ })).toBeInTheDocument();
  });
});
