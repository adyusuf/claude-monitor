import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { codeLink, issueSummary, issueView, link } from "../test/issues-fixtures";
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
  "GET /issues/i1/runs": { body: [] },
  ...over,
});
const open = async () => {
  renderAt("/w/w1/projects/p1/issues/i1", route);
  await screen.findByRole("heading", { level: 1 });
};

describe("pull requests and commits", () => {
  it("lists them; a web address is a safe link, anything else stays text, an automatic one is marked", async () => {
    serve({
      "GET /issues/i1/code-links": { body: [
        codeLink(),
        codeLink({ id: "c2", kind: "commit", url: "javascript:alert(1)", title: null, source: "auto" }),
        codeLink({ id: "c3", url: "data:text/html,<script>alert(1)</script>", title: "<b>x</b>" }),
      ] },
    });
    await open();
    const anchor = await screen.findByRole("link", { name: "https://git.example.org/shop/pull/7" });
    expect(anchor).toHaveAttribute("href", "https://git.example.org/shop/pull/7");
    expect(anchor).toHaveAttribute("target", "_blank");
    expect(anchor).toHaveAttribute("rel", "noopener noreferrer");
    expect(screen.getByText("Card payments")).toBeInTheDocument();
    const manual = anchor.closest("li")!;
    expect(within(manual).getByText("Pull request")).toBeInTheDocument();
    expect(within(manual).queryByText("Automatic")).not.toBeInTheDocument();
    const auto = screen.getByText("javascript:alert(1)").closest("li")!;
    expect(within(auto).getByText("Commit")).toBeInTheDocument();
    expect(within(auto).getByText("Automatic")).toBeInTheDocument();
    expect(screen.getByText("javascript:alert(1)").closest("a")).toBeNull();
    expect(screen.getByText("data:text/html,<script>alert(1)</script>").closest("a")).toBeNull();
    expect(screen.getByText("<b>x</b>")).toBeInTheDocument();
    expect(document.querySelector("strong b")).toBeNull();
  });

  it("says when there are none, and when the read failed", async () => {
    serve();
    await open();
    expect(await screen.findByText("No pull requests or commits linked.")).toBeInTheDocument();
  });

  it("shows a failed read", async () => {
    serve({ "GET /issues/i1/code-links": { status: 500 } });
    await open();
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });

  it("adds a link and clears the form", async () => {
    const calls = serve({ "POST /issues/i1/code-links": { status: 201, body: codeLink() } });
    await open();
    await userEvent.selectOptions(await screen.findByLabelText("Kind"), "commit");
    await userEvent.type(screen.getByLabelText("Address (https)"), " https://git.example.org/shop/commit/abc ");
    await userEvent.type(screen.getByLabelText("Title (optional)"), "Fix rounding");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ kind: "commit", url: "https://git.example.org/shop/commit/abc", title: "Fix rounding" }));
    await waitFor(() => expect(screen.getByLabelText("Address (https)")).toHaveValue(""));
    expect(screen.getByLabelText("Title (optional)")).toHaveValue("");
  });

  it("sends no title when none was typed", async () => {
    const calls = serve({ "POST /issues/i1/code-links": { status: 201, body: codeLink() } });
    await open();
    await userEvent.type(await screen.findByLabelText("Address (https)"), "http://git.example.org/pr/1");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.body).toEqual({ kind: "pr", url: "http://git.example.org/pr/1", title: null }));
  });

  it.each(["javascript:alert(1)", "https://user:secret@git.example.org/pr/1", "not an address", "ftp://git.example.org/x"])(
    "refuses %s before asking the API", async (address) => {
      const calls = serve();
      await open();
      await userEvent.type(await screen.findByLabelText("Address (https)"), address);
      await userEvent.click(screen.getByRole("button", { name: "Add" }));
      expect(await screen.findByText(/That address is not allowed here/)).toBeInTheDocument();
      expect(calls.some((c) => c.method === "POST")).toBe(false);
    },
  );

  it("shows the API's refusal and keeps what was typed", async () => {
    serve({ "POST /issues/i1/code-links": { status: 400, body: { errors: { url: ["limit_reached"] } } } });
    await open();
    await userEvent.type(await screen.findByLabelText("Address (https)"), "https://git.example.org/pr/1");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(await screen.findByText("The most that fits here has been reached.")).toBeInTheDocument();
    expect(screen.getByLabelText("Address (https)")).toHaveValue("https://git.example.org/pr/1");
  });

  it("removes a link", async () => {
    const calls = serve({ "GET /issues/i1/code-links": { body: [codeLink()] }, "DELETE /code-links/c1": { status: 204 } });
    await open();
    await userEvent.click(await screen.findByRole("button", { name: "Remove: Card payments" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/code-links/c1")).toBe(true));
  });

  it("shows no add form or remove button to a reader", async () => {
    serve({ "GET /issues/i1": { body: issueView({ canEdit: false }) }, "GET /issues/i1/code-links": { body: [codeLink()] } });
    await open();
    await screen.findByText("Card payments");
    expect(screen.queryByLabelText("Address (https)")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Remove: / })).not.toBeInTheDocument();
  });
});
