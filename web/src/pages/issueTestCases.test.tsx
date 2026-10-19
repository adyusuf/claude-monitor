import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { TestCaseView } from "../api/typesIssues";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary, issueView, link, testCase } from "../test/issues-fixtures";
import { appSummary, project, workspaceMembers } from "../test/projects-fixtures";
import { IssuePage } from "./IssuePage";

const route = [{ path: "/w/:ws/projects/:projectId/issues/:id", element: <IssuePage /> }];
const found = [issueSummary(), issueSummary({ id: "i2", number: 2, title: "Refund an order" }), issueSummary({ id: "i3", number: 3, title: "Print a receipt" })];

const serve = (over: Parameters<typeof mockApi>[0] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /issues/i1": { body: issueView({ blocks: [link()], blockedBy: [link({ linkId: "l2", issueId: "i3", number: 3, title: "Print a receipt", status: "done" })] }) },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": { body: { items: [appSummary()], next: null } },
  "GET /projects/p1/issues": (c) => ({ body: { items: c.path.includes("q=") ? found : [], next: null } }),
  "GET /issues/i1/test-cases": { body: [] },
  "GET /issues/i1/code-links": { body: [] },
  ...over,
});
const open = async () => {
  renderAt("/w/w1/projects/p1/issues/i1", route);
  await screen.findByRole("heading", { level: 1 });
};

describe("test cases", () => {
  it("shows the scenarios with the result, who set it and when, and the note", async () => {
    serve({
      "GET /issues/i1/test-cases": { body: [
        testCase({ result: "failed", resultNote: "Card was declined", resultByName: "Deniz", resultAt: "2026-02-05T09:30:00Z" }),
        testCase({ id: "t2", title: "Pays in cash", steps: null, expected: null, result: "passed", resultByName: null, resultAt: "2026-02-06T09:30:00Z" }),
      ] },
    });
    await open();
    const first = (await screen.findByText("Pays by card")).closest("li")!;
    expect(first.querySelector(".badge")).toHaveTextContent("Failed");
    expect(within(first).getByText("Open the cart")).toBeInTheDocument();
    expect(within(first).getByText("Order is paid")).toBeInTheDocument();
    expect(within(first).getByText(/Result set by Deniz on 0[45]\/02\/2026/)).toBeInTheDocument();
    expect(within(first).getByText("Card was declined")).toBeInTheDocument();
    const second = screen.getByText("Pays in cash").closest("li")!;
    expect(second.querySelector(".badge")).toHaveTextContent("Passed");
    expect(within(second).getByText(/Result set by – on 0[56]\/02\/2026/)).toBeInTheDocument();
  });

  it("shows an empty state and a failed read", async () => {
    serve({ "GET /issues/i1/test-cases": { status: 500 } });
    await open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("says when there are none", async () => {
    serve();
    await open();
    expect(await screen.findByText("No test cases yet.")).toBeInTheDocument();
  });

  it("sets a result with a note and shows the new result", async () => {
    let state: TestCaseView = testCase();
    const calls = serve({
      "GET /issues/i1/test-cases": () => ({ body: [state] }),
      "POST /test-cases/t1/result": (c) => {
        const body = c.body as { result: TestCaseView["result"]; note: string | null };
        state = { ...state, result: body.result, resultNote: body.note, resultByName: "Örnek Kişi", resultAt: "2026-02-07T12:00:00Z" };
        return { status: 204 };
      },
    });
    await open();
    await userEvent.type(await screen.findByLabelText("Note on the result Pays by card"), "  works ");
    await userEvent.click(screen.getByRole("button", { name: "Passed: Pays by card" }));
    expect(await screen.findByText(/Result set by Örnek Kişi/)).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/test-cases/t1/result")?.body).toEqual({ result: "passed", note: "works" });
    await userEvent.clear(screen.getByLabelText("Note on the result Pays by card"));
    await userEvent.click(screen.getByRole("button", { name: "Blocked: Pays by card" }));
    await waitFor(() => expect(calls.filter((c) => c.path === "/test-cases/t1/result").at(-1)?.body).toEqual({ result: "blocked", note: null }));
  });

  it("shows the refusal when a result is refused", async () => {
    serve({ "GET /issues/i1/test-cases": { body: [testCase()] }, "POST /test-cases/t1/result": { status: 403 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Not run: Pays by card" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });

  it("adds a scenario and clears the form; a blank title is refused first", async () => {
    const calls = serve({ "POST /issues/i1/test-cases": { status: 201, body: testCase() } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Add test case" }));
    expect(await screen.findByText("Enter a name (up to 100 characters).")).toBeInTheDocument();
    expect(calls.some((c) => c.method === "POST")).toBe(false);
    await userEvent.type(screen.getByLabelText("Scenario"), " Pays by card ");
    await userEvent.type(screen.getByLabelText("Steps"), "Open the cart");
    await userEvent.click(screen.getByRole("button", { name: "Add test case" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ title: "Pays by card", steps: "Open the cart", expected: null }));
    await waitFor(() => expect(screen.getByLabelText("Scenario")).toHaveValue(""));
    expect(screen.getByLabelText("Steps")).toHaveValue("");
    expect(screen.queryByText("Enter a name (up to 100 characters).")).not.toBeInTheDocument();
  });

  it("keeps what was typed when adding is refused", async () => {
    serve({ "POST /issues/i1/test-cases": { status: 400, body: { errors: { title: ["limit_reached"] } } } });
    await open();
    await userEvent.type(await screen.findByLabelText("Scenario"), "Pays");
    await userEvent.click(screen.getByRole("button", { name: "Add test case" }));
    expect(await screen.findByText("The most that fits here has been reached.")).toBeInTheDocument();
    expect(screen.getByLabelText("Scenario")).toHaveValue("Pays");
  });

  it("edits a scenario, and Cancel leaves it alone", async () => {
    const calls = serve({ "GET /issues/i1/test-cases": { body: [testCase()] }, "PUT /test-cases/t1": { status: 204 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Edit: Pays by card" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.getByText("Order is paid")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Edit: Pays by card" }));
    const title = screen.getByLabelText("Scenario Pays by card");
    expect(title).toHaveValue("Pays by card");
    await userEvent.clear(title);
    await userEvent.type(title, "Pays online");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ title: "Pays online", steps: "Open the cart", expected: "Order is paid" }));
    await waitFor(() => expect(screen.queryByLabelText("Scenario Pays by card")).not.toBeInTheDocument());
  });

  it("deletes a scenario only after a confirmation", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValueOnce(false).mockReturnValue(true);
    const calls = serve({ "GET /issues/i1/test-cases": { body: [testCase()] }, "DELETE /test-cases/t1": { status: 204 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Delete: Pays by card" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Delete: Pays by card" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/test-cases/t1")).toBe(true));
    expect(confirm).toHaveBeenCalledWith("Delete the test case \"Pays by card\"?");
  });

  it("shows the scenarios read-only to a reader", async () => {
    serve({ "GET /issues/i1": { body: issueView({ canEdit: false }) }, "GET /issues/i1/test-cases": { body: [testCase()] } });
    await open();
    await screen.findByText("Pays by card");
    expect(screen.queryByRole("button", { name: /Passed|Edit|Delete/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Scenario")).not.toBeInTheDocument();
  });
});
