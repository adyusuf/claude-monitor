import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { AccessPolicySummary } from "../api/typesAccess";
import { ME, mockApi, renderAt } from "../test/helpers";
import { text } from "./machinesFixture";
import { SessionAccessLine } from "./SessionAccessLine";

const policy = (over: Partial<AccessPolicySummary> = {}): AccessPolicySummary => ({ projectId: "p7", projectName: "Widgets", version: 3, isSigned: true, shell: false, ...over });
const show = (projectKey: string | null | undefined) =>
  renderAt("/w/w1/sessions/s1", [{ path: "/w/:ws/sessions/:id", element: <div data-testid="line"><SessionAccessLine ws="w1" projectKey={projectKey} /></div> }]);

describe("the access line on a session page", () => {
  it("names the policy that covers the repository, its version, that it is signed and that shell runs are off, linking to the project", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/access-policies/by-key": { body: policy() } });
    show("acme/widgets");
    const link = await screen.findByRole("link", { name: "Access: policy of Widgets, version 3" });
    expect(link).toHaveAttribute("href", "/w/w1/projects/p7");
    expect(text(screen.getByTestId("line"))).toBe("Access: policy of Widgets, version 3 · Signed · no shell");
    expect(calls.find((c) => c.path.startsWith("/workspaces/w1/access-policies/by-key"))!.path).toBe("/workspaces/w1/access-policies/by-key?projectKey=acme%2Fwidgets");
  });

  it("says the policy is not signed and that shell is allowed when that is what it says", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/access-policies/by-key": { body: policy({ isSigned: false, shell: true }) } });
    show("acme/widgets");
    await screen.findByRole("link", { name: /Access: policy of Widgets/ });
    expect(text(screen.getByTestId("line"))).toBe("Access: policy of Widgets, version 3 · Not signed · shell allowed");
  });

  it("says that no policy covers the repository when the API answers 404", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/access-policies/by-key": { status: 404, body: {} } });
    show("acme/widgets");
    expect(await screen.findByText("Access: no policy covers this repository")).toBeInTheDocument();
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("shows the error text, not 'no policy', when the read fails for another reason", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/access-policies/by-key": { status: 500, body: {} } });
    show("acme/widgets");
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.queryByText("Access: no policy covers this repository")).toBeNull();
  });

  it("shows nothing and asks nothing for a session that names no repository", async () => {
    const calls = mockApi({ "GET /me": { body: ME } });
    show(null);
    await screen.findByTestId("line");
    expect(screen.getByTestId("line")).toBeEmptyDOMElement();
    expect(calls.some((c) => c.path.includes("access-policies"))).toBe(false);
  });
});
