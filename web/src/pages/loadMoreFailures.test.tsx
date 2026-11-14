import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { appSummary, project, projectMember, summary, workspaceMembers } from "../test/projects-fixtures";
import { ProjectPage } from "./ProjectPage";
import { ProjectsPage } from "./ProjectsPage";

describe("loading the next page when the server fails", () => {
  it("keeps the projects already shown and shows the error", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/projects": (call) => (call.path.includes("cursor=c1") ? { status: 500 } : { body: { items: [summary()], next: "c1" } }),
    });
    renderAt("/w/w1/projects", [{ path: "/w/:ws/projects", element: <ProjectsPage /> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Checkout" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument(); // the person can try again
  });

  it("keeps the applications already shown and shows the error", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /projects/p1": { body: project() },
      "GET /workspaces/w1/members": { body: workspaceMembers },
      "GET /projects/p1/members": { body: [projectMember()] },
      "GET /projects/p1/sprints": { body: { items: [], next: null } },
      "GET /projects/p1/applications": (call) => (call.path.includes("cursor=n1") ? { status: 500 } : { body: { items: [appSummary()], next: "n1" } }),
    });
    renderAt("/w/w1/projects/p1", [{ path: "/w/:ws/projects/:id", element: <ProjectPage /> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Shop site" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument();
  });
});
