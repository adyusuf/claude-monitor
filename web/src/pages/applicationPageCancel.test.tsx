import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { application, component, release, workspaceMembers } from "../test/projects-fixtures";
import { ApplicationPage } from "./ApplicationPage";

describe("the application page: Cancel in the edit form", () => {
  it("closes the form, shows the definition again and sends nothing", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      "GET /applications/a1": { body: application() },
      "GET /workspaces/w1/members": { body: workspaceMembers },
      "GET /applications/a1/components": { body: [component()] },
      "GET /applications/a1/releases": { body: [release()] },
    });
    renderAt("/w/w1/projects/p1/applications/a1", [{ path: "/w/:ws/projects/:projectId/applications/:id", element: <ApplicationPage /> }]);
    await userEvent.click(await screen.findByRole("button", { name: "Edit" }));
    await userEvent.clear(await screen.findByLabelText("Application name"));
    await userEvent.type(screen.getByLabelText("Application name"), "Half-typed");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByLabelText("Application name")).toBeNull());
    expect(screen.getByRole("heading", { level: 1, name: /Shop site/ })).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });
});
