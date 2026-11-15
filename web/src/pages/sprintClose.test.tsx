import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { renderAt } from "../test/helpers";
import { sprintSummary, sprintView } from "../test/sprints-fixtures";
import { issuePage, serveSprint } from "../test/sprints-serve";
import { SprintPage } from "./SprintPage";

const route = [{ path: "/w/:ws/projects/:projectId/sprints/:id", element: <SprintPage /> }];
const open = (path = "/w/w1/projects/p1/sprints/s1") => renderAt(path, route);
const reads = (calls: { method: string; path: string }[]) => calls.filter((c) => c.method === "GET" && c.path === "/sprints/s1").length;
const without = (name: string | RegExp) => expect(screen.queryByRole("button", { name })).not.toBeInTheDocument();
const planned = (id = "s3") => sprintSummary({ id, number: 3, name: "Next" });

describe("the sprint page: closing", () => {
  const active = { "GET /sprints/s1": { body: sprintView({}, { status: "active" }) } };

  it("offers Close on an active sprint, not Start or Delete", async () => {
    serveSprint(active);
    open();
    await screen.findByText("Pay with a card");
    expect(screen.getByRole("button", { name: "Close sprint" })).toBeInTheDocument();
    without("Start");
    without("Delete");
  });

  it("asks where the unfinished issues go, counting them, and sends them back to the backlog", async () => {
    const calls = serveSprint({ ...active, "POST /sprints/s1/close": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    expect(await screen.findByText("2 issues are not finished. Where do they go?")).toBeInTheDocument();
    const target = screen.getByLabelText("Move the unfinished issues to");
    await waitFor(() => expect(within(target).getAllByRole("option")).toHaveLength(2));
    expect(within(target).getAllByRole("option").map((o) => o.textContent)).toEqual(["Back to the backlog", "Sprint 3 · Next"]);
    await userEvent.click(screen.getByRole("button", { name: "Close the sprint" }));
    await waitFor(() => expect(screen.queryByText("Where do they go?", { exact: false })).not.toBeInTheDocument());
    expect(calls.find((c) => c.path === "/sprints/s1/close")?.body).toEqual({ moveUnfinishedTo: null });
    expect(reads(calls)).toBe(2);
  });

  it("moves them to a planned sprint of the same scope, and offers no other sprint", async () => {
    const calls = serveSprint({
      ...active, "POST /sprints/s1/close": { status: 204 },
      "GET /projects/p1/sprints": issuePage([planned(), sprintSummary({ id: "s4", number: 4, name: "Of an app", applicationId: "a1" }), planned("s1")]),
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    const target = await screen.findByLabelText("Move the unfinished issues to");
    await waitFor(() => expect(within(target).getAllByRole("option")).toHaveLength(2));
    await userEvent.selectOptions(target, "s3");
    await userEvent.click(screen.getByRole("button", { name: "Close the sprint" }));
    await waitFor(() => expect(calls.some((c) => c.path === "/sprints/s1/close")).toBe(true));
    expect(calls.find((c) => c.path === "/sprints/s1/close")?.body).toEqual({ moveUnfinishedTo: "s3" });
    expect(calls.find((c) => c.path.startsWith("/projects/p1/sprints"))?.path).toBe("/projects/p1/sprints?status=planned&limit=25");
  });

  it("asks for the sprints of the application for an application's sprint", async () => {
    const calls = serveSprint({ "GET /sprints/s1": { body: sprintView({}, { status: "active", applicationId: "a1" }) } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    await screen.findByLabelText("Move the unfinished issues to");
    expect(calls.find((c) => c.path.startsWith("/projects/p1/sprints"))?.path).toBe("/projects/p1/sprints?status=planned&application=a1&limit=25");
  });

  it("asks nothing when every issue is finished", async () => {
    const calls = serveSprint({ "GET /sprints/s1": { body: sprintView({ statusCounts: { done: 2, cancelled: 1 } }, { status: "active" }) }, "POST /sprints/s1/close": { status: 204 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    expect(await screen.findByText("Every issue is finished.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Move the unfinished issues to")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Close the sprint" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/sprints/s1/close")?.body).toEqual({ moveUnfinishedTo: null }));
  });

  it("cancels without closing", async () => {
    const calls = serveSprint(active);
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(screen.queryByText("Close this sprint")).not.toBeInTheDocument();
    expect(calls.some((c) => c.path === "/sprints/s1/close")).toBe(false);
  });

  it("keeps the panel and shows the refusal when the target is refused", async () => {
    serveSprint({ ...active, "POST /sprints/s1/close": { status: 400, body: { errors: { moveUnfinishedTo: ["invalid_sprint"] } } } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    await userEvent.click(await screen.findByRole("button", { name: "Close the sprint" }));
    expect(await screen.findByText("Pick a planned sprint of the same scope.")).toBeInTheDocument();
    expect(screen.getByText("Close this sprint")).toBeInTheDocument();
  });

  it("closes the panel and says to reload when the sprint closed but the page could not be read again", async () => {
    let reads = 0;
    const calls = serveSprint({
      "GET /sprints/s1": () => (++reads === 1 ? { body: sprintView({}, { status: "active" }) } : { status: 500 }),
      "POST /sprints/s1/close": { status: 204 },
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    await userEvent.click(await screen.findByRole("button", { name: "Close the sprint" }));
    expect(await screen.findByText("The change was saved, but the page could not be reloaded. Reload the page.")).toBeInTheDocument();
    expect(screen.queryByText("Close this sprint")).not.toBeInTheDocument();
    expect(calls.filter((c) => c.path === "/sprints/s1/close")).toHaveLength(1);
  });

  it("offers the planned sprints of every page of the list as targets", async () => {
    const calls = serveSprint({
      ...active,
      "GET /projects/p1/sprints": (c) => (c.path.includes("cursor=c1")
        ? issuePage([sprintSummary({ id: "s4", number: 2, name: "Middle" }), sprintSummary({ id: "s5", number: 1, name: "Oldest" })])
        : issuePage([planned()], "c1")),
    });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    const target = await screen.findByLabelText("Move the unfinished issues to");
    await waitFor(() => expect(within(target).getAllByRole("option")).toHaveLength(4));
    expect(within(target).getAllByRole("option").map((o) => o.textContent)).toEqual(["Back to the backlog", "Sprint 3 · Next", "Sprint 2 · Middle", "Sprint 1 · Oldest"]);
    expect(calls.filter((c) => c.path.startsWith("/projects/p1/sprints")).map((c) => c.path))
      .toEqual(["/projects/p1/sprints?status=planned&limit=25", "/projects/p1/sprints?status=planned&limit=25&cursor=c1"]);
  });

  it("shows a failed read of the planned sprints", async () => {
    serveSprint({ ...active, "GET /projects/p1/sprints": { status: 500 } });
    open();
    await userEvent.click(await screen.findByRole("button", { name: "Close sprint" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
  });
});
