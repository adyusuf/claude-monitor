import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Me } from "../api/types";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent, count, settle } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const asRole = (role: "admin" | "viewer"): Me => ({ ...ME, workspaces: [{ id: "w1", name: "Team", role }, ME.workspaces[1]!] });
const open = () => renderAt("/w/w1/machines/a1", route);
const put = (calls: ReturnType<typeof mockApi>) => calls.filter((c) => c.method === "PUT" && c.path === "/agents/a1/role");

/** The machine page with every read it makes answered; the role answer and the machine are what a case is about. */
function api(over: Parameters<typeof mockApi>[0] = {}, view: Partial<ReturnType<typeof machine>> = {}, me: Me = ME) {
  return mockApi({
    "GET /me": { body: me },
    "GET /workspaces/w1/machines": { body: [machine({ roleEnvironment: "test", roleFunction: "web", ...view })] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "PUT /agents/a1/role": { body: {} },
    ...over,
  });
}
const addEnv = async (v: string) => userEvent.selectOptions(await screen.findByLabelText(en.machines.role.addEnvironment), v);
const addFn = (v: string) => userEvent.selectOptions(screen.getByLabelText(en.machines.role.addFunction), v);
const remove = (v: string) => userEvent.click(screen.getByRole("button", { name: en.machines.role.removeValue.replace("{value}", v) }));
const chips = (label: string) => [...within(screen.getByRole("list", { name: label })).getAllByRole("listitem")].map((li) => li.textContent);
const save = () => userEvent.click(screen.getByRole("button", { name: en.machines.role.save }));
const body = (environment: string | null, fn: string | null, environments: string[], functions: string[]) => ({ environment, function: fn, environments, functions });

describe("the role editor on the machine page", () => {
  it("shows an owner the role as chips, changes it from the chips and saves it, then reads the machines again", async () => {
    const calls = api();
    open();
    await screen.findByLabelText(en.machines.role.addEnvironment);
    expect(chips(en.machines.role.environments)).toEqual(["test"]);
    expect(chips(en.machines.role.functions)).toEqual(["web"]);
    await settle(calls);
    const before = count(calls, "/workspaces/w1/machines");
    await addEnv("production");
    await remove("test");
    await addFn("database");
    await remove("web");
    await save();
    expect(await screen.findByText(en.machines.role.saved)).toBeInTheDocument();
    expect(put(calls)).toHaveLength(1);
    expect(put(calls)[0]!.body).toEqual(body("production", "database", ["production"], ["database"]));
    expect(put(calls)[0]!.body).not.toHaveProperty("code");
    await waitFor(() => expect(count(calls, "/workspaces/w1/machines")).toBe(before + 1));
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });

  it("keeps several values, loosest first, and sends the strictest as the single value", async () => {
    const calls = api();
    open();
    await addEnv("dev");
    await addFn("database");
    expect(chips(en.machines.role.environments)).toEqual(["dev", "test"]);
    expect(chips(en.machines.role.functions)).toEqual(["web", "database"]);
    await save();
    await screen.findByText(en.machines.role.saved);
    expect(put(calls)[0]!.body).toEqual(body("test", "database", ["dev", "test"], ["web", "database"]));
  });

  it("changes a value from its chip's edit list", async () => {
    const calls = api();
    open();
    await screen.findByLabelText(en.machines.role.addEnvironment);
    await userEvent.click(screen.getByRole("button", { name: en.machines.role.editValue.replace("{value}", "test") }));
    await userEvent.selectOptions(screen.getByRole("combobox", { name: en.machines.role.editValue.replace("{value}", "test") }), "stage");
    expect(chips(en.machines.role.environments)).toEqual(["stage"]);
    await save();
    await screen.findByText(en.machines.role.saved);
    expect(put(calls)[0]!.body).toEqual(body("stage", "web", ["stage"], ["web"]));
  });

  it("names every value of a role with several in the badge", async () => {
    api({}, { roleEnvironments: ["dev", "test"], roleEnvironment: "test", effectiveEnvironment: "test", roleFunctions: ["web"], effectiveFunction: "web" }, asRole("viewer"));
    open();
    expect(await screen.findByText("dev+test/web")).toBeInTheDocument();
  });

  it("still says the role was saved when the re-read machine now carries the new role", async () => {
    let saved = false;
    api({
      "PUT /agents/a1/role": () => {
        saved = true;
        return { body: {} };
      },
      "GET /workspaces/w1/machines": () => ({ body: [machine(saved ? { roleEnvironment: "production", roleFunction: "web" } : { roleEnvironment: "test", roleFunction: "web" })] }),
    });
    open();
    await addEnv("production");
    await remove("test");
    await save();
    await waitFor(() => expect(chips(en.machines.role.environments)).toEqual(["production"]));
    expect(screen.getByText(en.machines.role.saved)).toBeInTheDocument();
  });

  it("offers an admin the chips too", async () => {
    api({}, {}, asRole("admin"));
    open();
    expect(await screen.findByLabelText(en.machines.role.addEnvironment)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.role.save })).toBeInTheDocument();
  });

  it("sends empty sets and null single values for a role whose chips were all removed", async () => {
    const calls = api();
    open();
    await screen.findByLabelText(en.machines.role.addEnvironment);
    await remove("test");
    await remove("web");
    await save();
    await screen.findByText(en.machines.role.saved);
    expect(put(calls)[0]!.body).toEqual(body(null, null, [], []));
  });

  it("shows 'not set' for both sets of a machine with no role and keeps save off until something changes", async () => {
    api({}, { roleEnvironment: null, roleFunction: null });
    open();
    await screen.findByLabelText(en.machines.role.addEnvironment);
    expect(screen.queryByRole("list", { name: en.machines.role.environments })).toBeNull();
    expect(screen.getByRole("button", { name: en.machines.role.save })).toBeDisabled();
  });

  it("shows a viewer the role as text and no chips to change or save button", async () => {
    api({}, { roleEnvironment: "production", effectiveEnvironment: "production", effectiveFunction: "web" }, asRole("viewer"));
    open();
    expect(await screen.findByText(en.machines.role.adminOnly)).toBeInTheDocument();
    expect(screen.queryByLabelText(en.machines.role.addEnvironment)).toBeNull();
    expect(screen.queryByLabelText(en.machines.role.addFunction)).toBeNull();
    expect(screen.queryByRole("button", { name: en.machines.role.save })).toBeNull();
    expect(screen.getByText("production/web")).toHaveClass("status-failed");
    expect(screen.getByText(en.machines.role.strict)).toBeInTheDocument();
  });

  it("shows the machine's own claim beside the role and says it only tightens", async () => {
    api({}, { claimedEnvironment: "production", claimedFunction: null });
    open();
    expect(await screen.findByText(`${en.machines.role.claimed.replace("{role}", "production/not set")} ${en.machines.role.claimTightens}`)).toBeInTheDocument();
  });

  it("shows the API's text for a role the server refuses, and no success", async () => {
    const calls = api({ "PUT /agents/a1/role": { status: 400, body: { title: "invalid_machine_role" } } });
    open();
    await addEnv("dev");
    await save();
    expect(await screen.findByText(en.errors.invalid_machine_role)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.role.saved)).toBeNull();
    expect(put(calls)).toHaveLength(1);
  });
});

describe("lowering a role needs a recent sign-in", () => {
  /** The first save is refused with reauth_required; later ones are answered by `then`. */
  const reauthFirst = (then: (n: number) => { status?: number; body?: unknown }) => {
    let n = 0;
    return { "PUT /agents/a1/role": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : then(n)) };
  };
  const lower = async () => {
    await addEnv("dev");
    await remove("production");
  };

  it("asks for a code with the lowering hint, then sends it with the same role and succeeds", async () => {
    const calls = api(reauthFirst(() => ({ body: {} })), { roleEnvironment: "production", roleFunction: "database" });
    open();
    await lower();
    await save();
    const field = await screen.findByLabelText(en.remote.codeLabel);
    expect(screen.getByText(en.machines.role.lowerHint)).toBeInTheDocument();
    expect(screen.queryByText(en.errors.invalid_code)).toBeNull();
    expect(screen.queryByText(en.machines.role.saved)).toBeNull();
    expect(put(calls)[0]!.body).toEqual(body("dev", "database", ["dev"], ["database"]));
    expect(put(calls)[0]!.body).not.toHaveProperty("code");
    await userEvent.type(field, " 123456 ");
    await save();
    expect(await screen.findByText(en.machines.role.saved)).toBeInTheDocument();
    expect(put(calls)).toHaveLength(2);
    expect(put(calls)[1]!.body).toEqual({ ...body("dev", "database", ["dev"], ["database"]), code: "123456" });
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });

  it("says the code was wrong when the API asks again after one was sent", async () => {
    const calls = api(reauthFirst(() => ({ status: 403, body: { title: "reauth_required" } })), { roleEnvironment: "production" });
    open();
    await lower();
    await save();
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "000000");
    await save();
    expect(await screen.findByText(en.errors.invalid_code)).toBeInTheDocument();
    expect(put(calls)[1]!.body).toMatchObject({ code: "000000" });
    expect(screen.getByLabelText(en.remote.codeLabel)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.role.saved)).toBeNull();
  });

  it("shows the API's text for a role the server refuses once the code field is open", async () => {
    api(reauthFirst(() => ({ status: 400, body: { title: "invalid_machine_role" } })), { roleEnvironment: "production" });
    open();
    await lower();
    await save();
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await save();
    expect(await screen.findByText(en.errors.invalid_machine_role)).toBeInTheDocument();
    expect(screen.queryByText(en.machines.role.saved)).toBeNull();
  });
});
