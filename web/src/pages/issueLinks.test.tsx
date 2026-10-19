import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary, issueView, link } from "../test/issues-fixtures";
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
const card = (title: string) => screen.getByRole("heading", { name: title }).closest("section")!;

describe("blocks and blocked by", () => {
  it("lists both directions with links to the other issues", async () => {
    serve();
    await open();
    const blocks = screen.getByRole("region", { name: "Blocks" });
    expect(within(blocks).getByRole("link", { name: "#2 Refund an order" })).toHaveAttribute("href", "/w/w1/projects/p1/issues/i2");
    expect(within(blocks).getByText("Ready")).toBeInTheDocument();
    const blockedBy = screen.getByRole("region", { name: "Blocked by" });
    expect(within(blockedBy).getByRole("link", { name: "#3 Print a receipt" })).toBeInTheDocument();
    expect(within(blockedBy).getByText("Done")).toBeInTheDocument();
  });

  it("shows an empty state for each direction", async () => {
    serve({ "GET /issues/i1": { body: issueView() } });
    await open();
    expect(screen.getAllByText("None.")).toHaveLength(2);
  });

  it("removes a link and reads the issue again", async () => {
    let blocks = [link()];
    const calls = serve({
      "GET /issues/i1": () => ({ body: issueView({ blocks }) }),
      "DELETE /issue-links/l1": () => {
        blocks = [];
        return { status: 204 };
      },
    });
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Remove #2" }));
    await waitFor(() => expect(screen.queryByRole("link", { name: "#2 Refund an order" })).not.toBeInTheDocument());
    expect(calls.some((c) => c.method === "DELETE" && c.path === "/issue-links/l1")).toBe(true);
  });

  it("shows the refusal when a link cannot be removed", async () => {
    serve({ "DELETE /issue-links/l1": { status: 403 } });
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Remove #2" }));
    expect(await screen.findByText("You are not allowed to do that.")).toBeInTheDocument();
  });

  it("searches the project's issues, leaves the issue itself out and adds a link of the chosen kind", async () => {
    const calls = serve({ "POST /issues/i1/links": { status: 204 } });
    await open();
    expect(screen.getByRole("button", { name: "Add link" })).toBeDisabled();
    await userEvent.selectOptions(screen.getByLabelText("How the issues are linked"), "blocked_by");
    await userEvent.type(screen.getByLabelText("Search an issue to link"), "re");
    await waitFor(() => expect(within(screen.getByLabelText("Issue to link")).getAllByRole("option")).toHaveLength(3));
    expect(within(screen.getByLabelText("Issue to link")).getAllByRole("option").map((o) => o.textContent)).toEqual(["–", "#2 Refund an order", "#3 Print a receipt"]);
    expect(calls.some((c) => c.path.includes("q=re") && c.path.includes("limit=20"))).toBe(true);
    await userEvent.selectOptions(screen.getByLabelText("Issue to link"), "i2");
    await userEvent.click(screen.getByRole("button", { name: "Add link" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ otherIssueId: "i2", kind: "blocked_by" }));
    await waitFor(() => expect(screen.getByLabelText("Search an issue to link")).toHaveValue(""));
    expect(screen.getByLabelText("Issue to link")).toHaveValue("");
  });

  it("says when nothing matches the search", async () => {
    serve({ "GET /projects/p1/issues": () => ({ body: { items: [], next: null } }) });
    await open();
    await userEvent.type(screen.getByLabelText("Search an issue to link"), "zzz");
    expect(await screen.findByRole("option", { name: "No issue matches." })).toBeInTheDocument();
  });

  it("shows a failed search", async () => {
    serve({ "GET /projects/p1/issues": { status: 500 } });
    await open();
    await userEvent.type(screen.getByLabelText("Search an issue to link"), "re");
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it.each([
    ["self_link", "An issue cannot be linked to itself."],
    ["cycle", "That link would make the issues block each other in a loop."],
    ["duplicate_link", "These two issues are already linked."],
    ["invalid_issue", "Pick another issue of this project."],
  ])("shows %s as a notice and keeps the choice", async (code, text) => {
    serve({ "POST /issues/i1/links": { status: 400, body: { errors: { otherIssueId: [code] } } } });
    await open();
    await userEvent.type(screen.getByLabelText("Search an issue to link"), "re");
    await waitFor(() => expect(within(screen.getByLabelText("Issue to link")).getAllByRole("option")).toHaveLength(3));
    await userEvent.selectOptions(screen.getByLabelText("Issue to link"), "i2");
    await userEvent.click(screen.getByRole("button", { name: "Add link" }));
    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.getByLabelText("Issue to link")).toHaveValue("i2");
  });

  it("does not add when the form is sent without a choice", async () => {
    const calls = serve();
    await open();
    await userEvent.type(screen.getByLabelText("Search an issue to link"), "{Enter}");
    expect(calls.some((c) => c.method === "POST")).toBe(false);
  });

  it("shows no add form or remove buttons to a reader", async () => {
    serve({ "GET /issues/i1": { body: issueView({ canEdit: false, blocks: [link()] }) } });
    await open();
    expect(screen.queryByLabelText("Search an issue to link")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove #2" })).not.toBeInTheDocument();
    expect(card("Blocks and blocked by")).toBeInTheDocument();
  });
});
