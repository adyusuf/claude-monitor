import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { issueSummary, issueView } from "../test/issues-fixtures";
import { appSummary, component, project, release, workspaceMembers } from "../test/projects-fixtures";
import { IssuePage } from "./IssuePage";

const route = [{ path: "/w/:ws/projects/:projectId/issues/:id", element: <IssuePage /> }];
const mirror = { externalKey: "SHOP-12", externalUrl: "https://acme.atlassian.net/browse/SHOP-12" };
const epics = [issueSummary({ id: "e1", number: 9, title: "Payments epic", type: "epic" })];

const serve = (issue: Parameters<typeof issueView>[1] = {}) => mockApi({
  "GET /me": { body: ME },
  "GET /issues/i1": { body: issueView({}, issue) },
  "GET /projects/p1": { body: project() },
  "GET /workspaces/w1/members": { body: workspaceMembers },
  "GET /projects/p1/applications": { body: { items: [appSummary()], next: null } },
  "GET /projects/p1/issues": { body: { items: epics, next: null } },
  "GET /issues/i1/test-cases": { body: [] },
  "GET /issues/i1/code-links": { body: [] },
  "GET /issues/i1/runs": { body: [] },
  "GET /applications/a1/components": { body: [component()] },
  "GET /applications/a1/releases": { body: [release()] },
  "PUT /issues/i1": { status: 204 },
});
const open = (query = "") => renderAt(`/w/w1/projects/p1/issues/i1${query}`, route);

describe("the issue page of a mirrored issue", () => {
  it("shows the key beside the title as a link out, and no Archive button, but Edit stays", async () => {
    serve(mirror);
    open();
    const heading = await screen.findByRole("heading", { level: 1, name: /#1 Pay with a card/ });
    expect(heading).toContainElement(screen.getByRole("link", { name: "SHOP-12 ↗" }));
    expect(screen.getByRole("link", { name: "SHOP-12 ↗" })).toHaveAttribute("href", mirror.externalUrl);
    expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive" })).not.toBeInTheDocument();
  });

  it("shows no badge and keeps the Archive button for an issue made in Podium", async () => {
    serve();
    open();
    await screen.findByRole("heading", { level: 1, name: /#1 Pay with a card/ });
    expect(screen.queryByText(/SHOP-12/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Archive" })).toBeInTheDocument();
  });

  it("shows the key as text, not a link, when the address is not https", async () => {
    serve({ externalKey: "SHOP-12", externalUrl: "http://acme.atlassian.net/browse/SHOP-12" });
    open();
    await screen.findByRole("heading", { level: 1, name: /#1 Pay with a card/ });
    expect(screen.getByText("SHOP-12")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /SHOP-12/ })).not.toBeInTheDocument();
  });
});

describe("the issue form of a mirrored issue", () => {
  it("disables the Type select, keeps the other fields open, and still sends the type it has", async () => {
    const calls = serve(mirror);
    open("?edit=1");
    expect(await screen.findByLabelText("Type")).toBeDisabled();
    expect(screen.getByLabelText("Title")).toBeEnabled();
    expect(screen.getByLabelText("Priority")).toBeEnabled();
    await userEvent.clear(screen.getByLabelText("Title"));
    await userEvent.type(screen.getByLabelText("Title"), "Pay by card");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ title: "Pay by card", type: "story" });
  });

  it("leaves the Type select open for an issue made in Podium", async () => {
    serve();
    open("?edit=1");
    expect(await screen.findByLabelText("Type")).toBeEnabled();
  });
});
