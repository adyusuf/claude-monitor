import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ApprovalRequestRow } from "../api/types";
import { en } from "../i18n/en";
import { type Call, mockApi } from "../test/helpers";
import { request } from "../test/approvals-fixtures";
import { renderPlain } from "../test/remote-fixtures";
import { ApprovalItem } from "./ApprovalItem";

const show = (row: ApprovalRequestRow, onDone = vi.fn()) => {
  renderPlain(<ApprovalItem ws="w1" row={row} onDone={onDone} />);
  return onDone;
};
const answers = (calls: Call[]) => calls.filter((c) => c.method === "POST" && c.path === "/permission-requests/q1/answer");
const remember = () => screen.getByRole("combobox", { name: en.approvalRules.remember });
const allow = () => screen.getByRole("button", { name: en.approvalRules.allow });
const deny = () => screen.getByRole("button", { name: en.approvalRules.deny });

describe("what an approval card shows", () => {
  it("shows the tool, when it waits until, the session it comes from as a link, the machine and the project", () => {
    show(request());
    expect(screen.getByText("Bash")).toBeInTheDocument();
    expect(screen.getByText(`${en.approvalRules.expires}: 12:34:56`)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Fix the login" })).toHaveAttribute("href", "/w/w1/sessions/s1");
    expect(screen.getByText(/laptop-1/)).toBeInTheDocument();
    expect(screen.getByText(/widgets/)).toBeInTheDocument();
  });

  it("says 'Untitled session' for a session with no title and leaves out a missing project", () => {
    show(request({ sessionTitle: null, projectName: null }));
    expect(screen.getByRole("link", { name: en.approvalRules.untitled })).toBeInTheDocument();
    expect(screen.queryByText(/widgets/)).toBeNull();
  });

  it("marks a request on a strict machine", () => {
    show(request({ strict: true, canRemember: false }));
    expect(screen.getByText(en.machines.role.strict)).toHaveClass("warn");
  });

  it("shows the whole command of a Bash request in a block of its own", () => {
    show(request({ toolInput: { command: "rm -rf build && npm ci", description: "clean" } }));
    expect(screen.getByText("rm -rf build && npm ci").tagName).toBe("PRE");
  });

  it("shows the other keys of a Bash call, so the person sees all that runs, but not its description", () => {
    show(request({ toolInput: { command: "npm test", description: "run them", run_in_background: true } }));
    expect(screen.getByText('{"run_in_background":true}')).toBeInTheDocument();
    expect(screen.queryByText(/run them/)).toBeNull();
  });

  it("shows the input of any other tool as it is", () => {
    show(request({ toolName: "Write", toolInput: { file_path: "/srv/app/a.ts" } }));
    expect(screen.getByText('{"file_path":"/srv/app/a.ts"}')).toBeInTheDocument();
  });

  it("shows a plan to approve or send back, with those words on the buttons", () => {
    show(request({ toolName: "ExitPlanMode", toolInput: { plan: "Step one" } }));
    expect(screen.getByText("Step one")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.chat.approvePlan })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: en.chat.rejectPlan })).toBeInTheDocument();
  });

  it("sends the person to the session for a question and offers no Allow or Deny", () => {
    show(request({ toolName: "AskUserQuestion", toolInput: { questions: [] } }));
    expect(screen.getByText(en.approvalRules.questionOnSession, { exact: false })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: en.approvalRules.openSession })).toHaveAttribute("href", "/w/w1/sessions/s1");
    expect(screen.queryByRole("button", { name: en.approvalRules.allow })).toBeNull();
    expect(screen.queryByRole("button", { name: en.approvalRules.deny })).toBeNull();
  });
});

describe("how long an allow stands", () => {
  it("offers six durations from 'This time only', and the plain hint, when the request may be remembered", () => {
    show(request());
    const options = within(remember()).getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["This time only", "5 minutes", "10 minutes", "30 minutes", "1 hour", "Always"]);
    expect(remember()).toHaveValue("once");
    expect(screen.getByText(en.approvalRules.rememberHint)).toBeInTheDocument();
  });

  it("offers no selector and says why for a request that cannot have a standing approval", () => {
    show(request({ canRemember: false }));
    expect(screen.queryByRole("combobox", { name: en.approvalRules.remember })).toBeNull();
    expect(screen.getByText(en.approvalRules.noRemember)).toBeInTheDocument();
  });

  it("says each request on a production or database machine is decided on its own", () => {
    show(request({ canRemember: false, strict: true }));
    expect(screen.getByText(en.approvalRules.strictNoRemember)).toBeInTheDocument();
    expect(screen.queryByText(en.approvalRules.noRemember)).toBeNull();
  });

  it("changes the hint to the one about 'Always' when it is chosen", async () => {
    show(request());
    await userEvent.selectOptions(remember(), "always");
    expect(screen.getByText(en.approvalRules.rememberHintAlways)).toBeInTheDocument();
    expect(screen.queryByText(en.approvalRules.rememberHint)).toBeNull();
  });
});

describe("answering from the card", () => {
  it("allows once with no duration, and tells the page which request was answered", async () => {
    const calls = mockApi({ "POST /permission-requests/q1/answer": { status: 204 } });
    const onDone = show(request());
    await userEvent.click(allow());
    await waitFor(() => expect(onDone).toHaveBeenCalledWith("q1"));
    expect(answers(calls)[0]!.body).toEqual({ decision: "allow" });
  });

  it("allows for the chosen duration, without a code", async () => {
    const calls = mockApi({ "POST /permission-requests/q1/answer": { status: 204 } });
    const onDone = show(request());
    await userEvent.selectOptions(remember(), "1h");
    await userEvent.click(allow());
    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(answers(calls)[0]!.body).toEqual({ decision: "allow", remember: "1h" });
  });

  it("denies with the typed reason, trimmed, and never asks to remember a denial", async () => {
    const calls = mockApi({ "POST /permission-requests/q1/answer": { status: 204 } });
    const onDone = show(request());
    await userEvent.selectOptions(remember(), "1h");
    await userEvent.type(screen.getByLabelText(en.approvalRules.reason), "  not now  ");
    await userEvent.click(deny());
    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(answers(calls)[0]!.body).toEqual({ decision: "deny", reason: "not now" });
  });

  it("shows the API's error and does not tell the page when the answer fails", async () => {
    mockApi({ "POST /permission-requests/q1/answer": { status: 500, body: {} } });
    const onDone = show(request());
    await userEvent.click(allow());
    expect(await screen.findByRole("alert")).toHaveTextContent("Something went wrong on the server.");
    expect(onDone).not.toHaveBeenCalled();
  });
});

describe("'Always' needs the person's code", () => {
  const needsCode = (call: Call) => ((call.body as { code?: string }).code ? { status: 204 } : { status: 403, body: { title: "reauth_required" } });

  it("tries without a code first, then asks for one when the API says the sign-in is old, and holds the Allow button until it is typed", async () => {
    const calls = mockApi({ "POST /permission-requests/q1/answer": needsCode });
    show(request());
    await userEvent.selectOptions(remember(), "always");
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
    await userEvent.click(allow());
    expect(await screen.findByLabelText(en.remote.codeLabel)).toBeInTheDocument();
    expect(answers(calls)).toHaveLength(1);
    expect((answers(calls)[0]!.body as { code?: string }).code).toBeUndefined();
    expect(allow()).toBeDisabled();
  });

  it("sends the code with the always-allow once it is typed, and tells the page when it is accepted", async () => {
    const calls = mockApi({ "POST /permission-requests/q1/answer": needsCode });
    const onDone = show(request());
    await userEvent.selectOptions(remember(), "always");
    await userEvent.click(allow());
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "123456");
    await userEvent.click(allow());
    await waitFor(() => expect(onDone).toHaveBeenCalledWith("q1"));
    expect(answers(calls)[1]!.body).toEqual({ decision: "allow", remember: "always", code: "123456" });
  });

  it("says the code is not valid when the API asks again after one was sent", async () => {
    mockApi({ "POST /permission-requests/q1/answer": { status: 403, body: { title: "reauth_required" } } });
    const onDone = show(request());
    await userEvent.selectOptions(remember(), "always");
    await userEvent.click(allow());
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "000000");
    await userEvent.click(allow());
    expect(await screen.findByRole("alert")).toHaveTextContent(en.errors.invalid_code);
    expect(onDone).not.toHaveBeenCalled();
  });

  it("sends no code with a denial, even with 'Always' chosen", async () => {
    const calls = mockApi({ "POST /permission-requests/q1/answer": { status: 204 } });
    const onDone = show(request());
    await userEvent.selectOptions(remember(), "always");
    await userEvent.click(deny());
    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(answers(calls)[0]!.body).toEqual({ decision: "deny" });
  });

  it("asks for no code at all for a shorter duration", async () => {
    mockApi({ "POST /permission-requests/q1/answer": { status: 204 } });
    show(request());
    await userEvent.selectOptions(remember(), "30m");
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });
});
