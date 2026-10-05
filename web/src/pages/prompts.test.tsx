import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, call, said } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const detail = (canCommand = true) => ({
  session: { id: "s1", harnessKind: "claude_code", title: "Prompts", model: null, gitBranch: null, status: "waiting", startedAt: at, lastEventAt: at, endedAt: null,
    projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 1 },
  canCommand, tasks: [], subagents: [], usage: [],
});
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];
const waiting = (toolName: string, toolInput: Record<string, unknown>) => ({
  id: "p1", toolName, toolInput, status: "open", createdAt: at, expiresAt: at, decision: null, reason: null, answeredAt: null,
});
const options = (...labels: string[]) => labels.map((label) => ({ label, description: `about ${label}` }));
const way = { question: "Which way?", header: "Way", multiSelect: false, options: options("Left", "Right") };
const extras = { question: "Which extras?", header: "Extras", multiSelect: true, options: options("Tests", "Docs") };

function open(permission: unknown, canCommand = true) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /sessions/s1": { body: detail(canCommand) },
    "GET /sessions/s1/commands": { body: [] },
    "GET /sessions/s1/events": { body: { items: [said(1, "hello"), call(2, "q", "AskUserQuestion", { questions: [way] })].reverse(), next: null } },
    "GET /sessions/s1/permission-requests": { body: [permission] },
    "POST /permission-requests/p1/answer": { status: 204 },
  });
}
const answered = (calls: ReturnType<typeof open>) => calls.find((c) => c.path === "/permission-requests/p1/answer")?.body;

describe("answering Claude's question and plan from the web", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("answers a single question with one click on an option", async () => {
    const calls = open(waiting("AskUserQuestion", { questions: [way] }));
    renderAt("/w/w1/sessions/s1", route);
    const group = await screen.findByRole("group", { name: "Which way?" });
    expect(screen.queryByRole("button", { name: "Allow" })).toBeNull(); // the options are the answer
    await userEvent.click(within(group).getByRole("button", { name: /Right/ }));
    await waitFor(() => expect(answered(calls)).toEqual({ decision: "allow", answers: { "Which way?": "Right" } }));
    await waitFor(() => expect(screen.queryByRole("group", { name: "Which way?" })).toBeNull());
  });

  it("collects the choices of several questions, with typed text, and sends them together", async () => {
    const calls = open(waiting("AskUserQuestion", { questions: [way, extras] }));
    renderAt("/w/w1/sessions/s1", route);
    const send = await screen.findByRole("button", { name: "Send answers" });
    expect(send).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: /Left/ }));
    await userEvent.click(screen.getByRole("button", { name: /Tests/ }));
    await userEvent.click(screen.getByRole("button", { name: /Docs/ }));
    await userEvent.click(screen.getByRole("button", { name: /Docs/ })); // toggled off again
    await userEvent.click(screen.getByRole("button", { name: /Right/ })); // a single-choice question keeps one
    await userEvent.type(screen.getAllByPlaceholderText("Or type another answer")[1]!, "Lint");
    expect(send).toBeEnabled();
    await userEvent.click(send);
    await waitFor(() => expect(answered(calls)).toEqual({ decision: "allow", answers: { "Which way?": "Right", "Which extras?": "Tests, Lint" } }));
  });

  it("can decline a question, and offers a viewer nothing to press", async () => {
    const calls = open(waiting("AskUserQuestion", { questions: [way] }));
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Deny" }));
    await waitFor(() => expect(answered(calls)).toEqual({ decision: "deny" }));
  });

  it("shows a plan waiting for approval with approve and send-back buttons", async () => {
    const calls = open(waiting("ExitPlanMode", { plan: "# Plan\n- do the thing" }));
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText("do the thing")).toBeInTheDocument();
    await userEvent.type(screen.getByPlaceholderText("Reason (optional)"), "split it first");
    await userEvent.click(screen.getByRole("button", { name: "Send back with the note above" }));
    await waitFor(() => expect(answered(calls)).toEqual({ decision: "deny", reason: "split it first" }));
  });

  it("approves a plan", async () => {
    const calls = open(waiting("ExitPlanMode", { plan: "- step" }));
    renderAt("/w/w1/sessions/s1", route);
    await userEvent.click(await screen.findByRole("button", { name: "Approve the plan" }));
    await waitFor(() => expect(answered(calls)).toEqual({ decision: "allow" }));
  });

  it("gives someone who is not the owner the question without buttons to answer it", async () => {
    open(waiting("AskUserQuestion", { questions: [way] }), false);
    renderAt("/w/w1/sessions/s1", route);
    expect(await screen.findByText(/Only the person whose machine runs this session/, { selector: ".permission p" })).toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "Which way?" })).toBeNull();
  });
});
