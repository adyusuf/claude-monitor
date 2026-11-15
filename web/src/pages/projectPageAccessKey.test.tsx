import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Link } from "react-router";
import { en } from "../i18n/en";
import { defaultView } from "../test/access-fixtures";
import { ME, mockApi, renderAt } from "../test/helpers";
import { project, workspaceMembers } from "../test/projects-fixtures";
import { ProjectPage } from "./ProjectPage";

const route = [{ path: "/w/:ws/projects/:id", element: <><Link to="/w/w1/projects/p2">next project</Link><ProjectPage /></> }];

describe("the project page: the access card follows the project", () => {
  it("shows the second project's policy after the first project's card was left out (404)", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /workspaces/w1/members": { body: workspaceMembers },
      "GET /projects/p1": { body: project() },
      "GET /projects/p2": { body: project({ id: "p2", name: "Payments" }) },
      "GET /projects/p1/access-policy": { status: 404 },
      "GET /projects/p2/access-policy": { body: defaultView({ projectId: "p2" }) },
    });
    renderAt("/w/w1/projects/p1", route);
    expect(await screen.findByRole("heading", { level: 1, name: /Checkout/ })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText(en.access.title)).toBeNull());
    await userEvent.click(screen.getByRole("link", { name: "next project" }));
    expect(await screen.findByRole("heading", { level: 1, name: /Payments/ })).toBeInTheDocument();
    expect(await screen.findByText(en.access.title)).toBeInTheDocument();
  });
});
