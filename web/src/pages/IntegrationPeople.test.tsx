import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ME, mockApi, renderAt, type Call } from "../test/helpers";
import { person } from "../test/integrations-fixtures";
import { workspaceMembers } from "../test/projects-fixtures";
import { IntegrationPeople } from "./IntegrationPeople";

const peoplePath = "GET /integrations/c1/people";
const savePath = "PUT /integrations/people/pe1";

/** Every route this part calls, plus the sign-in check the providers make. */
const serve = (routes: Parameters<typeof mockApi>[0]) => mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/members": { body: workspaceMembers }, ...routes });
const open = () => renderAt("/", [{ path: "/", element: <IntegrationPeople id="c1" ws="w1" /> }]);
const rowOf = async (name: string) => (await screen.findByText(name, { exact: false })).closest("li")!;
const saves = (calls: Call[]) => calls.filter((c) => c.method === "PUT");

describe("the people of a connection", () => {
  it("shows every person with the badges that tell where they stand", async () => {
    serve({
      [peoplePath]: {
        body: [
          person({ id: "pe1", displayName: "Mia Jira", email: null, invited: false }),
          person({ id: "pe2", displayName: "Ines Invited", email: "ines+test@gmail.com", invited: true }),
          person({ id: "pe3", displayName: "Max Member", email: "max+test@gmail.com", userId: "u2", userName: "Deniz" }),
          person({ id: "pe4", displayName: "Ida Inactive", email: "ida+test@gmail.com", active: false }),
        ],
      },
    });
    open();
    const mia = within(await rowOf("Mia Jira"));
    expect(mia.getByText("no e-mail")).toHaveClass("badge", "warn");
    expect(mia.queryByText("invited")).not.toBeInTheDocument();
    expect(mia.queryByText("inactive")).not.toBeInTheDocument();
    const ines = within(await rowOf("Ines Invited"));
    expect(ines.getByText("ines+test@gmail.com")).toBeInTheDocument();
    expect(ines.getByText("invited")).toHaveClass("badge");
    expect(ines.queryByText("no e-mail")).not.toBeInTheDocument();
    const max = within(await rowOf("Max Member"));
    expect(max.getByText("member: Deniz")).toHaveClass("badge", "ok");
    expect(within(await rowOf("Ida Inactive")).getByText("inactive")).toHaveClass("badge");
    expect(within(await rowOf("Ines Invited")).queryByText("inactive")).not.toBeInTheDocument();
  });

  it("offers the e-mail field and the member chooser only for a person who is not linked to a member yet", async () => {
    serve({ [peoplePath]: { body: [person(), person({ id: "pe3", displayName: "Max Member", userId: "u2", userName: "Deniz" })] } });
    open();
    const free = within(await rowOf("Mia Jira"));
    expect(free.getByLabelText("E-mail")).toBeInTheDocument();
    expect(free.getByLabelText("Link to a member")).toBeInTheDocument();
    const linked = within(await rowOf("Max Member"));
    expect(linked.queryByLabelText("E-mail")).not.toBeInTheDocument();
    expect(linked.queryByLabelText("Link to a member")).not.toBeInTheDocument();
  });

  it("keeps Save off until an e-mail is typed, saves the trimmed address and loads the people again", async () => {
    let listed = 0;
    const calls = serve({
      [peoplePath]: () => ({ body: ++listed === 1 ? [person({ email: null })] : [person({ email: "mia+new@gmail.com" })] }),
      [savePath]: { status: 204 },
    });
    open();
    const row = within(await rowOf("Mia Jira"));
    expect(row.getByRole("button", { name: "Save" })).toBeDisabled();
    await userEvent.type(row.getByLabelText("E-mail"), "   ");
    expect(row.getByRole("button", { name: "Save" })).toBeDisabled(); // blanks are no address
    await userEvent.type(row.getByLabelText("E-mail"), " mia+new@gmail.com ");
    await userEvent.click(row.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(listed).toBe(2));
    expect(saves(calls).map((c) => c.body)).toEqual([{ email: "mia+new@gmail.com" }]);
    expect(await screen.findByText("mia+new@gmail.com")).toBeInTheDocument();
    expect(screen.queryByText("no e-mail")).not.toBeInTheDocument();
  });

  it("keeps each person's typed address apart", async () => {
    serve({ [peoplePath]: { body: [person(), person({ id: "pe2", displayName: "Ben Jira" })] } });
    open();
    const first = within(await rowOf("Mia Jira"));
    const second = within(await rowOf("Ben Jira"));
    await userEvent.type(first.getByLabelText("E-mail"), "a@x.org");
    expect(first.getByLabelText("E-mail")).toHaveValue("a@x.org");
    expect(second.getByLabelText("E-mail")).toHaveValue("");
    expect(second.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("links a person to the chosen member and loads the people again", async () => {
    let listed = 0;
    const calls = serve({
      [peoplePath]: () => ({ body: ++listed === 1 ? [person()] : [person({ userId: "u2", userName: "Deniz" })] }),
      [savePath]: { status: 204 },
    });
    open();
    const row = within(await rowOf("Mia Jira"));
    expect(within(row.getByLabelText("Link to a member")).getAllByRole("option").map((o) => o.textContent)).toEqual(["Link to a member", "Örnek Kişi", "Deniz", "Gözlemci"]);
    await userEvent.selectOptions(row.getByLabelText("Link to a member"), "u2");
    expect(await screen.findByText("member: Deniz")).toBeInTheDocument();
    expect(saves(calls).map((c) => c.body)).toEqual([{ userId: "u2" }]);
    expect(screen.queryByLabelText("Link to a member")).not.toBeInTheDocument();
  });

  it("does not save when the empty choice is picked", async () => {
    const calls = serve({ [peoplePath]: { body: [person()] }, [savePath]: { status: 204 } });
    open();
    const row = within(await rowOf("Mia Jira"));
    await userEvent.selectOptions(row.getByLabelText("Link to a member"), "u2");
    await userEvent.selectOptions(row.getByLabelText("Link to a member"), "");
    expect(saves(calls)).toHaveLength(1); // only the real choice
  });

  it("shows the translated problem when saving fails, and keeps the list", async () => {
    serve({ [peoplePath]: { body: [person()] }, [savePath]: { status: 400, body: { title: "invalid_email" } } });
    open();
    const row = within(await rowOf("Mia Jira"));
    await userEvent.type(row.getByLabelText("E-mail"), "nope");
    await userEvent.click(row.getByRole("button", { name: "Save" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That does not look like an e-mail address.");
    expect(screen.getByText("Mia Jira")).toBeInTheDocument();
  });

  it("shows only the translated problem when the people cannot be loaded", async () => {
    serve({ [peoplePath]: { status: 403, body: { title: "forbidden" } } });
    open();
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("shows the hint about invitations above the list", async () => {
    serve({ [peoplePath]: { body: [] } });
    open();
    expect(await screen.findByText(/is invited as a viewer when invitations are on/)).toBeInTheDocument();
  });
});
