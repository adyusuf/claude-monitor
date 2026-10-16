import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineDetail, MachineProjectView, Me } from "../api/types";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { noteOk, parseTags, tagsText } from "./MachineFacts";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const asRole = (role: "admin" | "viewer"): Me => ({ ...ME, workspaces: [{ id: "w1", name: "Team", role }, ME.workspaces[1]!] });
const open = () => renderAt("/w/w1/machines/a1", route);

const project = (over: Partial<MachineProjectView> = {}): MachineProjectView => ({
  key: "git:host.example/team/shop", name: "shop", branch: "main", headSha: "0123456789abcdef", tag: "v1.2.0", lastSeenAt: new Date(2026, 9, 5, 9, 30).toISOString(),
  source: "listed", recentErrors: 3, lastRuns: [], ...over,
});
const detail = (over: Partial<MachineDetail> = {}): MachineDetail => ({
  machine: machine(), note: null, noteSetAt: null, servicesShared: false, services: null, servicesAt: null, projects: [], ...over,
});

/** The machine page with every read answered; the detail and the machine are what a case is about. */
function api(over: Parameters<typeof mockApi>[0] = {}, view: Partial<ReturnType<typeof machine>> = {}, me: Me = ME, d: MachineDetail = detail(), userId = "u1") {
  return mockApi({
    "GET /me": { body: me },
    "GET /workspaces/w1/machines": { body: [machine(view)] },
    "GET /workspaces/w1/agents": { body: [agent({ userId })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    "GET /agents/a1/detail": { body: d },
    ...over,
  });
}
const puts = (calls: ReturnType<typeof mockApi>, what: string) => calls.filter((c) => c.method === "PUT" && c.path === `/agents/a1/${what}`);

describe("the tag and note helpers", () => {
  it("reads key=value pairs separated by spaces or commas and refuses a malformed pair or too many", () => {
    expect(parseTags("project=shop, region=eu  tier=1")).toEqual({ project: "shop", region: "eu", tier: "1" });
    expect(parseTags("")).toEqual({});
    expect(parseTags("novalue")).toBeNull();
    expect(parseTags("a=b=c")).toBeNull();
    expect(parseTags("=v")).toBeNull();
    expect(parseTags("k=ü")).toBeNull();
    expect(parseTags(`k=${"x".repeat(33)}`)).toBeNull();
    expect(parseTags(Array.from({ length: 11 }, (_, i) => `k${i}=v`).join(" "))).toBeNull();
    expect(parseTags(Array.from({ length: 10 }, (_, i) => `k${i}=v`).join(" "))).not.toBeNull();
  });

  it("writes the tags back sorted and accepts a note without control characters", () => {
    expect(tagsText({ region: "eu", project: "shop" })).toBe("project=shop region=eu");
    expect(tagsText(null)).toBe("");
    expect(noteOk("line one\nline two\ttab")).toBe(true);
    expect(noteOk("bell\u0007")).toBe(false);
    expect(noteOk("x".repeat(2001))).toBe(false);
  });
});

describe("tags and the note on the machine page", () => {
  it("shows an admin the tags in an input, saves them as pairs and reads the machine again", async () => {
    const calls = api({ "PUT /agents/a1/tags": { body: {} } }, { tags: { region: "eu" } });
    open();
    const input = await screen.findByLabelText(en.machines.tags.label);
    expect(input).toHaveValue("region=eu");
    await userEvent.clear(input);
    await userEvent.type(input, "project=shop region=eu");
    await userEvent.click(screen.getByRole("button", { name: en.machines.tags.save }));
    expect(await screen.findByText(en.machines.tags.saved)).toBeInTheDocument();
    expect(puts(calls, "tags")[0]!.body).toEqual({ tags: { project: "shop", region: "eu" } });
  });

  it("refuses a malformed tag on the client and keeps the save button off", async () => {
    const calls = api();
    open();
    const input = await screen.findByLabelText(en.machines.tags.label);
    await userEvent.type(input, "no-value");
    expect(screen.getByText(en.errors.invalid_tag)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.tags.save })).toBeDisabled();
    expect(puts(calls, "tags")).toHaveLength(0);
  });

  it("shows the API's refusal of a tag set", async () => {
    api({ "PUT /agents/a1/tags": { status: 400, body: { title: "invalid_tag" } } });
    open();
    await userEvent.type(await screen.findByLabelText(en.machines.tags.label), "a=b");
    await userEvent.click(screen.getByRole("button", { name: en.machines.tags.save }));
    expect(await screen.findByText(en.errors.invalid_tag)).toBeInTheDocument();
  });

  it("shows a viewer the tags as chips and the note as text, with no form", async () => {
    api({}, { tags: { project: "shop" } }, asRole("viewer"), detail({ note: "Restart the database first." }));
    open();
    expect(await screen.findByText("project=shop")).toHaveClass("badge");
    expect(screen.getByText("Restart the database first.")).toBeInTheDocument();
    expect(screen.queryByLabelText(en.machines.tags.label)).toBeNull();
    expect(screen.queryByLabelText(en.machines.note.label)).toBeNull();
  });

  it("says so when a viewer sees no tags and no note", async () => {
    api({}, {}, asRole("viewer"));
    open();
    expect(await screen.findByText(en.machines.tags.none)).toBeInTheDocument();
    expect(screen.getByText(en.machines.note.none)).toBeInTheDocument();
  });

  it("saves the note and sends null when it is emptied", async () => {
    const calls = api({ "PUT /agents/a1/note": { body: {} } }, {}, ME, detail({ note: "old" }));
    open();
    const box = await screen.findByLabelText(en.machines.note.label);
    expect(box).toHaveValue("old");
    await userEvent.clear(box);
    await userEvent.click(screen.getByRole("button", { name: en.machines.note.save }));
    expect(await screen.findByText(en.machines.note.saved)).toBeInTheDocument();
    expect(puts(calls, "note")[0]!.body).toEqual({ note: null });
  });

  it("refuses a note with a control character on the client", async () => {
    api();
    open();
    const box = await screen.findByLabelText(en.machines.note.label);
    await userEvent.type(box, "a\u0007b");
    expect(screen.getByText(en.errors.invalid_note)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.machines.note.save })).toBeDisabled();
  });

  it("does not render a note as markup", async () => {
    api({}, {}, asRole("viewer"), detail({ note: "<img src=x onerror=alert(1)> **bold**" }));
    const { container } = open();
    await screen.findByText(/onerror/);
    expect(container.querySelector("img")).toBeNull();
  });
});

describe("the listening services card", () => {
  it("tells the owner the report is off and turns it on without a code first", async () => {
    const calls = api({ "PUT /agents/a1/services": { body: {} } });
    open();
    expect(await screen.findByText(en.machines.services.off)).toBeInTheDocument();
    await userEvent.click(await screen.findByRole("button", { name: en.machines.services.turnOn })); // the owner is known once the signed-in user has loaded
    expect(await screen.findByText(en.machines.services.saved)).toBeInTheDocument();
    expect(puts(calls, "services")[0]!.body).toEqual({ share: true });
  });

  it("asks for a code when the API wants a recent sign-in and then sends it", async () => {
    let n = 0;
    const calls = api({ "PUT /agents/a1/services": () => (n++ === 0 ? { status: 403, body: { title: "reauth_required" } } : { body: {} }) });
    open();
    await userEvent.click(await screen.findByRole("button", { name: en.machines.services.turnOn }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await userEvent.click(screen.getByRole("button", { name: en.machines.services.turnOn }));
    await screen.findByText(en.machines.services.saved);
    expect(puts(calls, "services")[1]!.body).toEqual({ share: true, code: "123456" });
  });

  it("lists the ports with the process names for the owner and offers to stop", async () => {
    api({}, {}, ME, detail({ servicesShared: true, services: [{ port: 22, proto: "tcp", process: null }, { port: 5432, proto: "tcp", process: "postgres" }], servicesAt: new Date(2026, 9, 5, 9, 30).toISOString() }));
    open();
    expect(await screen.findByText("5432 postgres")).toBeInTheDocument();
    expect(screen.getByText("22")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: en.machines.services.turnOff })).toBeInTheDocument();
  });

  it("says the list exists but is hidden from someone who is neither owner nor admin", async () => {
    api({}, {}, asRole("viewer"), detail({ servicesShared: true, services: null }), "u2");
    open();
    expect(await screen.findByText(en.machines.services.hidden)).toBeInTheDocument();
    expect(screen.getAllByText(en.machines.services.ownerOnly)).toHaveLength(2); // the services card and the asks card say the same sentence
    expect(screen.queryByRole("button", { name: en.machines.services.turnOff })).toBeNull();
  });

  it("says the agent has not reported yet when the switch is on and the list is empty", async () => {
    api({}, {}, ME, detail({ servicesShared: true, services: [] }));
    open();
    expect(await screen.findByText(en.machines.services.waiting)).toBeInTheDocument();
  });
});

describe("the project history", () => {
  it("shows each project with its branch, commit, tag, errors and the last runs with their counts", async () => {
    api({}, {}, ME, detail({
      projects: [project({ lastRuns: [
        { kind: "test", status: "failed", exitCode: 1, at: new Date(2026, 9, 5, 9, 30).toISOString(), outputTruncated: false, testsPassed: 41, testsFailed: 1, testsSkipped: 2 },
        { kind: "build", status: "succeeded", exitCode: 0, at: new Date(2026, 9, 5, 9, 0).toISOString(), outputTruncated: true, testsPassed: null, testsFailed: null, testsSkipped: null },
      ] })],
    }));
    open();
    expect(await screen.findByText("shop")).toBeInTheDocument();
    expect(screen.getByText("main")).toBeInTheDocument();
    expect(screen.getByText("01234567")).toBeInTheDocument();
    expect(screen.getByText("v1.2.0")).toBeInTheDocument();
    expect(screen.getByText("3 tool errors in 7 days")).toBeInTheDocument();
    expect(screen.getByText("41 passed, 1 failed, 2 skipped")).toBeInTheDocument();
    expect(screen.getByText("failed (1)")).toHaveClass("status-failed");
    expect(screen.getByText("succeeded (0)")).toHaveClass("status-active");
    expect(screen.getByText(en.machines.projects.cut)).toBeInTheDocument();
  });

  it("says a project has no run yet and tells how a project appears when there is none", async () => {
    api({}, {}, ME, detail({ projects: [project({ source: "session", recentErrors: 0 })] }));
    open();
    expect(await screen.findByText(en.machines.projects.noRuns)).toBeInTheDocument();
    expect(screen.getByText(new RegExp(en.machines.projects.session))).toBeInTheDocument();
  });

  it("shows the empty state for a machine with no project", async () => {
    api();
    open();
    expect(await screen.findByText(en.machines.projects.none)).toBeInTheDocument();
  });

  it("still shows the machine when the API has no detail (an older server)", async () => {
    api({ "GET /agents/a1/detail": { status: 404, body: { title: "not_found" } } });
    open();
    expect(await screen.findByText("laptop-1")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText(en.machines.projects.title)).toBeNull());
  });
});
