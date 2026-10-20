import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ConnectionView } from "../api/typesIntegrations";
import { renderAt, type Call } from "../test/helpers";
import { connection, serveIntegrations as serve } from "../test/integrations-fixtures";
import { IntegrationCard } from "./IntegrationCard";

const open = (over: Partial<ConnectionView> = {}, admin = true) => {
  const onChange = vi.fn();
  renderAt("/", [{ path: "/", element: <IntegrationCard connection={connection(over)} ws="w1" admin={admin} onChange={onChange} /> }]);
  return onChange;
};
const runCalls = (calls: Call[]) => calls.filter((c) => c.method === "GET" && c.path === "/integrations/c1/runs").length;
const writes = (calls: Call[]) => calls.filter((c) => c.method !== "GET" && c.path.startsWith("/integrations/c1"));

describe("a connection card: the admin actions", () => {
  it("turns invitations on with the checkbox, tells the page and shows the box as the API has it", async () => {
    const calls = serve({ "PUT /integrations/c1": { status: 204 } });
    const onChange = open({ autoInvite: false });
    const box = await screen.findByRole("checkbox", { name: /Invite Jira people to Podium/ });
    expect(box).not.toBeChecked();
    await userEvent.click(box);
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(writes(calls)).toEqual([{ method: "PUT", path: "/integrations/c1", body: { autoInvite: true } }]);
  });

  it("turns invitations off when the box was on", async () => {
    const calls = serve({ "PUT /integrations/c1": { status: 204 } });
    open({ autoInvite: true });
    const box = await screen.findByRole("checkbox", { name: /Invite Jira people to Podium/ });
    expect(box).toBeChecked();
    await userEvent.click(box);
    await waitFor(() => expect(writes(calls)).toHaveLength(1));
    expect(writes(calls)[0]!.body).toEqual({ autoInvite: false });
  });

  it("shows the refusal when the switch cannot be saved and does not tell the page", async () => {
    serve({ "PUT /integrations/c1": { status: 403, body: { title: "forbidden" } } });
    const onChange = open();
    await userEvent.click(await screen.findByRole("checkbox", { name: /Invite Jira people/ }));
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
    expect(onChange).not.toHaveBeenCalled();
  });

  it("checks now: starts the check, tells the page and loads the recent checks again", async () => {
    const calls = serve({ "POST /integrations/c1/sync": { body: { id: "r2" } } });
    const onChange = open();
    await screen.findByText("Nothing has run yet.");
    expect(runCalls(calls)).toBe(1);
    await userEvent.click(screen.getByRole("button", { name: "Check now" }));
    await waitFor(() => expect(runCalls(calls)).toBe(2));
    expect(writes(calls)).toEqual([{ method: "POST", path: "/integrations/c1/sync", body: undefined }]);
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole("button", { name: "Check now" })).toBeEnabled(); // the busy state ends
  });

  it("shows the translated problem when the check fails", async () => {
    serve({ "POST /integrations/c1/sync": { status: 409, body: { title: "reauthorize" } } });
    const onChange = open();
    await userEvent.click(await screen.findByRole("button", { name: "Check now" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The connection needs a new sign-in at Atlassian.");
    expect(onChange).not.toHaveBeenCalled();
  });

  it("disconnects after the person says yes, naming the site in the question", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const calls = serve({ "DELETE /integrations/c1": { status: 204 } });
    const onChange = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    await waitFor(() => expect(onChange).toHaveBeenCalledTimes(1));
    expect(confirm).toHaveBeenCalledWith("Disconnect acme.atlassian.net? What was imported stays, but stops updating.");
    expect(writes(calls)).toEqual([{ method: "DELETE", path: "/integrations/c1", body: undefined }]);
  });

  it("does nothing when the person says no", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = serve({ "DELETE /integrations/c1": { status: 204 } });
    const onChange = open();
    await userEvent.click(await screen.findByRole("button", { name: "Disconnect" }));
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(writes(calls)).toEqual([]);
    expect(onChange).not.toHaveBeenCalled();
  });

  it("gives a person who is not an admin none of the actions, no invitation switch and no people list", async () => {
    const calls = serve();
    open({}, false);
    await screen.findByText("Nothing has run yet.");
    expect(screen.queryByRole("button", { name: "Check now" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Disconnect" })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /Invite Jira people/ })).not.toBeInTheDocument();
    expect(calls.some((c) => c.path === "/integrations/c1/people")).toBe(false);
  });
});
