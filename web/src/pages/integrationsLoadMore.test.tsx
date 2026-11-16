import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt, type Call } from "../test/helpers";
import { person, space } from "../test/integrations-fixtures";
import { workspaceMembers } from "../test/projects-fixtures";
import { IntegrationPeople } from "./IntegrationPeople";
import { IntegrationSpaces } from "./IntegrationSpaces";

const cut = { "X-Truncated": "true" };
const more = () => screen.queryByRole("button", { name: "Load more" });
const reads = (calls: Call[], path: string) => calls.filter((c) => c.method === "GET" && c.path.startsWith(path));

const openSpaces = async () => {
  renderAt("/", [{ path: "/", element: <IntegrationSpaces id="c1" ws="w1" canImport canImportSprints onImported={() => undefined} /> }]);
  await userEvent.click(await screen.findByRole("button", { name: "Show Jira spaces" }));
};
const openPeople = () => renderAt("/", [{ path: "/", element: <IntegrationPeople id="c1" ws="w1" /> }]);

describe("Load more on the Jira spaces the API cut (F-077)", () => {
  it("offers the button only beside the cut note, never under a whole list", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /integrations/c1/spaces": { body: [space()] } });
    await openSpaces();
    await screen.findByText("Shop");
    expect(more()).not.toBeInTheDocument();
  });

  it("reads the next spaces after those shown, adds each once, and drops the button on the last page", async () => {
    const calls = mockApi({
      "GET /me": { body: ME },
      // The second page repeats a space already shown (it moved between the reads): it is not listed twice.
      "GET /integrations/c1/spaces": (call) => (call.path.includes("skip=2")
        ? { body: [space({ key: "OPS", name: "Operations" }), space({ key: "WEB", name: "Website" })] }
        : { body: [space(), space({ key: "OPS", name: "Operations" })], headers: cut }),
    });
    await openSpaces();
    expect(await screen.findByText("Only the first 2 spaces are shown; the site has more.")).toBeInTheDocument();
    await userEvent.click(more()!);
    expect(await screen.findByText("Website")).toBeInTheDocument();
    expect(screen.getAllByText("Operations")).toHaveLength(1);
    expect(screen.getAllByRole("checkbox")).toHaveLength(3);
    expect(more()).not.toBeInTheDocument();
    expect(screen.queryByText(/Only the first/)).not.toBeInTheDocument();
    expect(reads(calls, "/integrations/c1/spaces").map((c) => c.path)).toEqual(["/integrations/c1/spaces", "/integrations/c1/spaces?skip=2"]);
  });

  it("keeps the button while the next page is cut too, and counts the rows now shown", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /integrations/c1/spaces": (call) => (call.path.includes("skip=1")
        ? { body: [space({ key: "WEB", name: "Website" })], headers: cut }
        : { body: [space()], headers: cut }),
    });
    await openSpaces();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Only the first 2 spaces are shown; the site has more.")).toBeInTheDocument();
    expect(more()).toBeInTheDocument();
  });

  it("shows the page's error when the next page fails, keeping the spaces shown and the button", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /integrations/c1/spaces": (call) => (call.path.includes("skip=") ? { status: 500 } : { body: [space()], headers: cut }),
    });
    await openSpaces();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByText("Shop")).toBeInTheDocument();
    expect(more()).toBeInTheDocument();
  });
});

describe("Load more on the people of a connection the API cut (F-077)", () => {
  const serve = (people: Parameters<typeof mockApi>[0][string]) =>
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/members": { body: workspaceMembers }, "GET /integrations/c1/people": people });

  it("offers no button under a whole list", async () => {
    serve({ body: [person()] });
    openPeople();
    await screen.findByText("Mia Jira", { exact: false });
    expect(more()).not.toBeInTheDocument();
  });

  it("adds the next people once each and drops the button on the last page", async () => {
    const calls = serve((call) => (call.path.includes("skip=2")
      ? { body: [person({ id: "pe2", displayName: "Ines Invited" }), person({ id: "pe3", displayName: "Max Member" })] }
      : { body: [person(), person({ id: "pe2", displayName: "Ines Invited" })], headers: cut }));
    openPeople();
    expect(await screen.findByText("Only the first 2 people are shown; the connection has more.")).toBeInTheDocument();
    await userEvent.click(more()!);
    expect(await screen.findByText("Max Member", { exact: false })).toBeInTheDocument();
    expect(screen.getAllByText("Ines Invited", { exact: false })).toHaveLength(1);
    await waitFor(() => expect(more()).not.toBeInTheDocument());
    expect(reads(calls, "/integrations/c1/people").map((c) => c.path)).toEqual(["/integrations/c1/people", "/integrations/c1/people?skip=2"]);
  });

  it("shows the page's error when the next page fails, keeping the people shown", async () => {
    serve((call) => (call.path.includes("skip=") ? { status: 500 } : { body: [person()], headers: cut }));
    openPeople();
    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));
    expect(await screen.findByText("Something went wrong on the server.")).toBeInTheDocument();
    expect(screen.getByText("Mia Jira", { exact: false })).toBeInTheDocument();
    expect(more()).toBeInTheDocument();
  });
});
