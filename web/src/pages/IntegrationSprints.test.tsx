import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ME, mockApi, renderAt } from "../test/helpers";
import { localIso, sprintChoice } from "../test/integrations-fixtures";
import { IntegrationSprints } from "./IntegrationSprints";

const listPath = "GET /integrations/c1/spaces/SHOP/sprints";
const importPath = "POST /integrations/c1/spaces/SHOP/sprints/import";
const threeSprints = () => [
  sprintChoice(),
  sprintChoice({ id: "11", name: "Sprint 11", state: "planned", start: null, end: null }),
  sprintChoice({ id: "9", name: "Sprint 9", state: "closed", start: localIso(2026, 2, 16, 9), end: localIso(2026, 2, 27, 17), imported: true, sprintId: "s9" }),
];

/** Every route this part calls, plus the sign-in check the providers make. */
const serve = (routes: Parameters<typeof mockApi>[0]) => mockApi({ "GET /me": { body: ME }, ...routes });
const open = (projectId: string | null = "p7") => {
  const onClose = vi.fn();
  renderAt("/", [
    { path: "/", element: <IntegrationSprints id="c1" spaceKey="SHOP" name="Shop" ws="w1" projectId={projectId} onClose={onClose} /> },
    { path: "/w/:ws/projects/:projectId/sprints/:sprintId", element: <div>sprint page</div> },
  ]);
  return onClose;
};

describe("the sprints of an imported space", () => {
  it("shows the sprints with a state badge each and the dates as dd/mm/yyyy, none for a sprint without dates", async () => {
    serve({ [listPath]: { body: threeSprints() } });
    open();
    expect(await screen.findByRole("heading", { name: "Sprints of Shop" })).toBeInTheDocument();
    expect(await screen.findByText("Sprint 10")).toBeInTheDocument();
    expect(screen.getByText("Active")).toHaveClass("badge");
    expect(screen.getByText("Planned")).toHaveClass("badge");
    expect(screen.getByText("Closed")).toHaveClass("badge");
    expect(screen.getByText("02/03/2026 – 12/03/2026")).toBeInTheDocument();
    expect(screen.getByText("16/02/2026 – 27/02/2026")).toBeInTheDocument();
    expect(screen.getAllByText(/\d{2}\/\d{2}\/\d{4}/)).toHaveLength(2);
  });

  it("shows only the one date that is known", async () => {
    serve({ [listPath]: { body: [sprintChoice({ end: null })] } });
    open();
    expect(await screen.findByText("02/03/2026 –")).toBeInTheDocument();
  });

  it("disables the imported sprint and links it to its sprint page", async () => {
    serve({ [listPath]: { body: threeSprints() } });
    open();
    expect(await screen.findByRole("checkbox", { name: "Select Sprint 9" })).toBeDisabled();
    expect(screen.getByRole("checkbox", { name: "Select Sprint 10" })).toBeEnabled();
    const link = screen.getByRole("link", { name: "Imported" });
    expect(link).toHaveAttribute("href", "/w/w1/projects/p7/sprints/s9");
    expect(screen.getAllByRole("link", { name: "Imported" })).toHaveLength(1);
    await userEvent.click(link);
    expect(await screen.findByText("sprint page")).toBeInTheDocument();
  });

  it("does not link an imported sprint when the project is unknown", async () => {
    serve({ [listPath]: { body: threeSprints() } });
    open(null);
    await screen.findByText("Sprint 9");
    expect(screen.queryByRole("link", { name: "Imported" })).not.toBeInTheDocument();
  });

  it("shows the empty text for a space without sprints", async () => {
    serve({ [listPath]: { body: [] } });
    open();
    expect(await screen.findByText("This space has no sprints (or no board).")).toBeInTheDocument();
  });

  it("shows the translated problem when the sprints cannot be loaded", async () => {
    serve({ [listPath]: { status: 404, body: { title: "space_not_found" } } });
    open();
    expect(await screen.findByRole("alert")).toHaveTextContent("That space is not in Jira (or this account cannot see it).");
  });

  it("imports the picked ids, says it is done, clears the picks and loads the list again", async () => {
    let listed = 0;
    const calls = serve({
      [listPath]: () => ({ body: ++listed === 1 ? threeSprints() : [sprintChoice({ imported: true, sprintId: "s10" }), ...threeSprints().slice(1)] }),
      [importPath]: { body: [{ sprintId: "10", outcome: "ok", created: 4, updated: 0 }] },
    });
    open();
    expect(await screen.findByRole("button", { name: "Import selected sprints (0)" })).toBeDisabled();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select Sprint 10" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Sprint 11" }));
    await userEvent.click(screen.getByRole("button", { name: "Import selected sprints (2)" }));
    expect(await screen.findByText("Done.")).toBeInTheDocument();
    expect(screen.getByText("Done.").closest(".notice")).toHaveClass("notice-success");
    expect(calls.find((c) => c.method === "POST")?.body).toEqual({ sprintIds: ["10", "11"] });
    await waitFor(() => expect(listed).toBe(2));
    expect(await screen.findByRole("checkbox", { name: "Select Sprint 10" })).toBeDisabled(); // now imported
    expect(screen.getByRole("button", { name: "Import selected sprints (0)" })).toBeDisabled();
  });

  it("shows the translated error of the first sprint that did not import instead of a success", async () => {
    serve({
      [listPath]: { body: threeSprints() },
      [importPath]: { body: [{ sprintId: "10", outcome: "ok", created: 1, updated: 0 }, { sprintId: "11", outcome: "tracker_rate_limited", created: 0, updated: 0 }] },
    });
    open();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select Sprint 10" }));
    await userEvent.click(screen.getByRole("button", { name: "Import selected sprints (1)" }));
    const notice = await screen.findByText("Jira asks to slow down. Try again in a minute.");
    expect(notice.closest(".notice")).toHaveClass("notice-error");
    expect(screen.queryByText("Done.")).not.toBeInTheDocument();
  });

  it("shows the translated problem when the whole import call fails", async () => {
    serve({ [listPath]: { body: threeSprints() }, [importPath]: { status: 409, body: { title: "reauthorize" } } });
    open();
    await userEvent.click(await screen.findByRole("checkbox", { name: "Select Sprint 10" }));
    await userEvent.click(screen.getByRole("button", { name: "Import selected sprints (1)" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The connection needs a new sign-in at Atlassian.");
    expect(screen.getByRole("button", { name: "Import selected sprints (1)" })).toBeEnabled();
  });

  it("calls onClose from the Close button", async () => {
    serve({ [listPath]: { body: [] } });
    const onClose = open();
    await userEvent.click(await screen.findByRole("button", { name: "Close" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
