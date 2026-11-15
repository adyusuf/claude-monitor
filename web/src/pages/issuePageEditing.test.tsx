import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueView, link } from "../test/issues-fixtures";
import { appSummary, project, workspaceMembers } from "../test/projects-fixtures";
import { IssuePage } from "./IssuePage";

const route = [{ path: "/w/:ws/projects/:projectId/issues/:id", element: <IssuePage /> }];

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /issues/i1": { body: issueView({ blocks: [link()], blockedBy: [link({ linkId: "l2", issueId: "i3", number: 3, title: "Print a receipt", status: "done" })] }) },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": { body: { items: [appSummary()], next: null } },
  "GET /projects/p1/issues": { body: { items: [], next: null } },
  "GET /issues/i1/test-cases": { body: [] },
  "GET /issues/i1/code-links": { body: [] },
  ...over,
});
const open = async () => {
  renderAt("/w/w1/projects/p1/issues/i1", route);
  await screen.findByRole("heading", { level: 1 });
};

describe("the issue page: the Edit button", () => {
  it("opens the form with the issue's title on Edit and closes it, unchanged, on Cancel", async () => {
    const calls = serve();
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Edit" }));
    expect(await screen.findByLabelText("Title")).toHaveValue("Pay with a card");
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByLabelText("Title")).toBeNull());
    expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });
});

describe("the issue page: removing a blocked-by link", () => {
  it("removes it through its own link id and reads the issue again", async () => {
    let blockedBy = [link({ linkId: "l2", issueId: "i3", number: 3, title: "Print a receipt", status: "done" })];
    const calls = serve({
      "GET /issues/i1": () => ({ body: issueView({ blockedBy }) }),
      "DELETE /issue-links/l2": () => {
        blockedBy = [];
        return { status: 204 };
      },
    });
    await open();
    await userEvent.click(within(screen.getByRole("region", { name: "Blocked by" })).getByRole("button", { name: "Remove #3" }));
    await waitFor(() => expect(screen.queryByRole("link", { name: "#3 Print a receipt" })).toBeNull());
    expect(calls.filter((c) => c.method === "DELETE").map((c) => c.path)).toEqual(["/issue-links/l2"]);
  });

  it("shows the refusal and keeps the link when the server refuses", async () => {
    serve({ "DELETE /issue-links/l2": { status: 403 } });
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Remove #3" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "#3 Print a receipt" })).toBeInTheDocument();
  });
});

describe("the issue page: adding a test case", () => {
  it("sends what was typed as the expected outcome, trimmed", async () => {
    const calls = serve({ "POST /issues/i1/test-cases": { status: 201, body: { id: "t9" } } });
    await open();
    await userEvent.type(await screen.findByLabelText("Scenario"), "Pays by card");
    await userEvent.type(screen.getByLabelText("Expected outcome"), "  Order is paid ");
    await userEvent.click(screen.getByRole("button", { name: "Add test case" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ title: "Pays by card", steps: null, expected: "Order is paid" }));
    await waitFor(() => expect(screen.getByLabelText("Expected outcome")).toHaveValue(""));
  });
});

describe("the issue page: following a link to another issue", () => {
  it("drops the previous issue's form and fields when the next issue fails to load", async () => {
    serve({ "GET /issues/i3": { status: 500 } });
    await open();
    expect(screen.getByRole("heading", { level: 1, name: /#1 Pay with a card/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("link", { name: "#3 Print a receipt" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { level: 1, name: /Pay with a card/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
  });
});
