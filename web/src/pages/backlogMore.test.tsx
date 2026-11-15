import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary, threeIssues } from "../test/issues-fixtures";
import { appSummary, project, workspaceMembers } from "../test/projects-fixtures";
import { BacklogPage } from "./BacklogPage";

const route = [{ path: "/w/:ws/projects/:projectId/backlog", element: <BacklogPage /> }];
const page = (items = threeIssues(), next: string | null = null) => ({ body: { items, next } });

describe("the backlog: a slow Load more", () => {
  it("does not append old rows to the list of a scope chosen while it was loading", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /projects/p1": { body: project() },
      "GET /workspaces/w1/members": { body: workspaceMembers },
      "GET /projects/p1/applications": { body: { items: [appSummary()], next: null } },
      "GET /projects/p1/issues": (c) => c.path.includes("archived=true") ? page([issueSummary({ id: "i9", number: 9, title: "Old and archived", archived: true })]) : page(threeIssues(), "c1"),
    });
    const answered = vi.mocked(globalThis.fetch).getMockImplementation()!;
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => { release = resolve; });
    vi.mocked(globalThis.fetch).mockImplementation(async (input, init) => {
      const text = String(input);
      if (!text.includes("cursor=c1")) return answered(input, init);
      await held;
      return new Response(JSON.stringify({ items: [issueSummary({ id: "i4", number: 4, title: "Stale row" })], next: null }), { status: 200, headers: { "Content-Type": "application/json" } });
    });
    renderAt("/w/w1/projects/p1/backlog", route);

    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    await userEvent.click(screen.getByRole("button", { name: "Archived" }));
    await screen.findByText("Old and archived");
    release();
    await waitFor(() => expect(vi.mocked(globalThis.fetch).mock.calls.some(([u]) => String(u).includes("cursor=c1"))).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(screen.queryByText("Stale row")).not.toBeInTheDocument();
    expect(screen.getByText("Old and archived")).toBeInTheDocument();
  });
});
