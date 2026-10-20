import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { space } from "../test/integrations-fixtures";
import { IntegrationSpaces } from "./IntegrationSpaces";

const spacesPath = "GET /integrations/c1/spaces";
const importPath = "POST /integrations/c1/spaces/import";
const twoSpaces = () => [space(), space({ key: "OPS", name: "Operations", imported: true, projectId: "p7" })];

/** Every route this part calls, plus the sign-in check the providers make. */
const serve = (routes: Parameters<typeof mockApi>[0]) => mockApi({ "GET /me": { body: ME }, ...routes });
const open = (canImport = true) => {
  const onImported = vi.fn();
  renderAt("/", [
    { path: "/", element: <IntegrationSpaces id="c1" ws="w1" canImport={canImport} onImported={onImported} /> },
    { path: "/w/:ws/projects/:projectId", element: <div>project page</div> },
  ]);
  return onImported;
};
const show = async () => userEvent.click(await screen.findByRole("button", { name: "Show Jira spaces" }));

describe("the spaces of a connection", () => {
  it("asks Jira for nothing until the show button is pressed", async () => {
    const calls = serve({ [spacesPath]: { body: twoSpaces() } });
    open();
    await screen.findByRole("button", { name: "Show Jira spaces" });
    expect(calls.some((c) => c.path === "/integrations/c1/spaces")).toBe(false);
    expect(screen.queryByText("Shop")).not.toBeInTheDocument();
  });

  it("lists the spaces after the button, linking an imported one to its project and offering its sprints", async () => {
    serve({ [spacesPath]: { body: twoSpaces() } });
    open();
    await show();
    expect(await screen.findByText("Shop")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Show Jira spaces" })).not.toBeInTheDocument();
    const imported = screen.getByRole("link", { name: "Open project" });
    expect(imported).toHaveAttribute("href", "/w/w1/projects/p7");
    expect(screen.getAllByRole("link", { name: "Open project" })).toHaveLength(1); // not for the space that is not imported
    expect(screen.getAllByRole("button", { name: "Sprints…" })).toHaveLength(1);
  });

  it("shows the empty text when the account sees no spaces, with nothing to import", async () => {
    serve({ [spacesPath]: { body: [] } });
    open();
    await show();
    expect(await screen.findByText("This account sees no spaces.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Import selected (0)" })).toBeDisabled();
  });

  it("shows the translated problem when Jira cannot be asked", async () => {
    serve({ [spacesPath]: { status: 502, body: { title: "tracker_unavailable" } } });
    open();
    await show();
    expect(await screen.findByRole("alert")).toHaveTextContent("Jira cannot be reached right now.");
    expect(screen.getByRole("button", { name: "Show Jira spaces" })).toBeEnabled(); // it can be asked again
  });

  it("counts the picked spaces on the import button, which is off while none is picked", async () => {
    serve({ [spacesPath]: { body: twoSpaces() } });
    open();
    await show();
    const button = await screen.findByRole("button", { name: "Import selected (0)" });
    expect(button).toBeDisabled();
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Shop" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Operations" }));
    expect(screen.getByRole("button", { name: "Import selected (2)" })).toBeEnabled();
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Shop" }));
    expect(screen.getByRole("button", { name: "Import selected (1)" })).toBeEnabled();
  });

  it("lets a person who may not import read the list but not pick, and shows no import button", async () => {
    serve({ [spacesPath]: { body: twoSpaces() } });
    open(false);
    await show();
    expect(await screen.findByRole("checkbox", { name: "Select Shop" })).toBeDisabled();
    expect(screen.getByRole("checkbox", { name: "Select Operations" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: /Import selected/ })).not.toBeInTheDocument();
  });

  it("imports the picked keys, shows one success notice per space, loads the list again and tells the page", async () => {
    let listed = 0;
    const calls = serve({
      [spacesPath]: () => ({ body: ++listed === 1 ? twoSpaces() : [space({ imported: true, projectId: "p8" }), twoSpaces()[1]] }),
      [importPath]: { body: [{ key: "SHOP", outcome: "ok", created: 12, updated: 3 }, { key: "OPS", outcome: "ok", created: 0, updated: 5 }] },
    });
    const onImported = open();
    await show();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select Shop" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Operations" }));
    await userEvent.click(screen.getByRole("button", { name: "Import selected (2)" }));
    expect(await screen.findByText("SHOP: 12 new, 3 updated")).toHaveTextContent("SHOP: 12 new, 3 updated");
    expect(screen.getByText("OPS: 0 new, 5 updated")).toBeInTheDocument();
    expect(screen.getAllByRole("status").filter((n) => n.className.includes("notice-success"))).toHaveLength(2);
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ keys: ["SHOP", "OPS"] });
    await waitFor(() => expect(onImported).toHaveBeenCalledTimes(1));
    expect(listed).toBe(2);
    expect(await screen.findAllByRole("link", { name: "Open project" })).toHaveLength(2); // Shop is a project now
    expect(screen.getByRole("button", { name: "Import selected (0)" })).toBeDisabled(); // the picks were cleared
  });

  it("shows an error notice for a space that failed beside the success of the others", async () => {
    serve({
      [spacesPath]: { body: twoSpaces() },
      [importPath]: { body: [{ key: "SHOP", outcome: "ok", created: 1, updated: 0 }, { key: "GONE", outcome: "space_not_found", created: 0, updated: 0 }] },
    });
    open();
    await show();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select Shop" }));
    await userEvent.click(screen.getByRole("button", { name: "Import selected (1)" }));
    const failed = await screen.findByText("GONE: That space is not in Jira (or this account cannot see it).");
    expect(failed.closest(".notice")).toHaveClass("notice-error");
    expect(screen.getByText("SHOP: 1 new, 0 updated").closest(".notice")).toHaveClass("notice-success");
  });

  it("shows the translated problem when the whole import call fails and keeps the picks", async () => {
    serve({ [spacesPath]: { body: twoSpaces() }, [importPath]: { status: 403, body: { title: "forbidden" } } });
    const onImported = open();
    await show();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select Shop" }));
    await userEvent.click(screen.getByRole("button", { name: "Import selected (1)" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
    expect(onImported).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Import selected (1)" })).toBeEnabled();
  });

  it("opens the sprint chooser of an imported space and closes it again", async () => {
    serve({ [spacesPath]: { body: twoSpaces() }, "GET /integrations/c1/spaces/OPS/sprints": { body: [] } });
    open();
    await show();
    await userEvent.click(await screen.findByRole("button", { name: "Sprints…" }));
    expect(await screen.findByRole("heading", { name: "Sprints of Operations" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("heading", { name: "Sprints of Operations" })).not.toBeInTheDocument();
  });

  it("goes to the project from the link", async () => {
    serve({ [spacesPath]: { body: twoSpaces() } });
    open();
    await show();
    const row = (await screen.findByText("Operations")).closest("li")!;
    await userEvent.click(within(row).getByRole("link", { name: "Open project" }));
    expect(await screen.findByText("project page")).toBeInTheDocument();
  });
});
