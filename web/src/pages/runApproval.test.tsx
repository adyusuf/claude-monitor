import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { mockApi } from "../test/helpers";
import { renderPlain, run } from "../test/remote-fixtures";
import { RunApproval } from "./RunApproval";

const iso = (ms: number) => new Date(Date.now() + ms).toISOString();

function show(r: WebRunView, onChanged = vi.fn()) {
  renderPlain(<RunApproval run={r} ws="w1" onChanged={onChanged} />);
  return onChanged;
}

const allow = () => screen.getByRole("button", { name: "Allow" });
const body = (calls: { method: string; path: string; body: unknown }[], path: string) => calls.find((c) => c.path === path)?.body;

describe("the approval card shows what will run", () => {
  it("puts every argv element in its own box, in order, and never shortens a long one", () => {
    const long = `/opt/${"x".repeat(900)}`;
    show(run({ argv: ["/usr/bin/tool", "--flag", "", long, "two words"] }));
    const boxes = within(screen.getByRole("list", { name: en.remote.argvList })).getAllByRole("listitem");
    expect(boxes).toHaveLength(5);
    expect(boxes[0]).toHaveTextContent("/usr/bin/tool");
    expect(boxes[1]).toHaveTextContent("--flag");
    expect(boxes[2]).toHaveTextContent(en.remote.emptyArg);
    expect(boxes[3]!.textContent).toBe(long);
    expect(boxes[4]).toHaveTextContent("two words");
  });

  it("makes hidden and non-ASCII characters visible inside the box", () => {
    show(run({ argv: ["/bin/echo", "a‮b", "​z", "pаypal"] }));
    const boxes = within(screen.getByRole("list", { name: en.remote.argvList })).getAllByRole("listitem");
    expect(boxes[1]).toHaveTextContent("a\\u202Eb");
    expect(boxes[1]!.querySelector("mark.vis-flag")).not.toBeNull();
    expect(boxes[2]).toHaveTextContent("\\u200Bz");
    expect(boxes[3]).toHaveTextContent("а \\u0430");
  });

  it("shows shell text whole, with its invisible characters escaped", () => {
    show(run({ mode: "shell", argv: null, shellCommand: "echo ok‮; rm -rf /", interpreter: true }));
    expect(screen.getByLabelText(en.remote.shellText)).toHaveTextContent("echo ok\\u202E; rm -rf /");
  });

  it("labels the reason as written by Claude and untrusted, and shows what is hidden in it", () => {
    show(run({ reason: "please‮ run" }));
    expect(screen.getByText(en.remote.reasonUntrusted)).toBeInTheDocument();
    expect(screen.getByText(/please/)).toHaveTextContent("please\\u202E run");
  });

  it("says no reason was given instead of showing an empty quote", () => {
    show(run({ reason: null }));
    expect(screen.getByText(en.remote.reasonUntrusted)).toBeInTheDocument();
    expect(screen.getByText(en.remote.reasonNone)).toBeInTheDocument();
  });

  it("shows target, working folder, time limit, grant and the requester with a link to the session", () => {
    show(run({ cwd: "/srv/app", timeoutSeconds: 90, grantId: "g-7", requesterSessionId: "s-5" }));
    expect(screen.getByText("Run on build-box")).toBeInTheDocument();
    expect(screen.getByText("/srv/app")).toBeInTheDocument();
    expect(screen.getByText("90 s")).toBeInTheDocument();
    expect(screen.getByText("g-7")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: en.remote.session })).toHaveAttribute("href", "/w/w1/sessions/s-5");
    expect(screen.getByText(/laptop-9/)).toBeInTheDocument();
  });

  it("says 'none' for no grant and no folder, and omits the session link without a session", () => {
    show(run({ cwd: null, grantId: null, requesterSessionId: null }));
    expect(screen.getByText(en.remote.grantNone)).toBeInTheDocument();
    expect(screen.getByText(en.remote.cwdNone)).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: en.remote.session })).toBeNull();
  });

  it("notes self-approval only when the requester is the approver", () => {
    show(run({ selfApproval: true }));
    expect(screen.getByText(en.remote.selfApproval)).toBeInTheDocument();
  });

  it("does not note self-approval for someone else's run", () => {
    show(run({ selfApproval: false }));
    expect(screen.queryByText(en.remote.selfApproval)).toBeNull();
  });

  it("warns when the asking session read remote output recently, and not otherwise", () => {
    show(run({ recentOutput: true }));
    expect(screen.getByText(en.remote.recentOutput)).toBeInTheDocument();
  });

  it("shows no recent-output warning, and no red notice, for an ordinary run", () => {
    show(run());
    expect(screen.queryByText(en.remote.recentOutput)).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("shows the red notice for a shell run and for an interpreter run, each with its own text", () => {
    show(run({ mode: "shell", argv: null, shellCommand: "ls", interpreter: true }));
    expect(screen.getByRole("alert")).toHaveTextContent(en.remote.interpreterShell);
  });

  it("shows the interpreter text for an argv run of an interpreter", () => {
    show(run({ argv: ["/usr/bin/python3", "-c", "1"], interpreter: true }));
    expect(screen.getByRole("alert")).toHaveTextContent(en.remote.interpreterArgv);
  });
});

describe("the Allow button", () => {
  afterEach(() => vi.useRealTimers());

  it("waits three seconds for an interpreter or shell run, then enables", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
    show(run({ interpreter: true, mode: "shell", argv: null, shellCommand: "ls" }));
    expect(config.runAllowDelayMs).toBe(3000);
    expect(allow()).toBeDisabled();
    expect(screen.getByText(en.remote.allowWait)).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(config.runAllowDelayMs - 1));
    expect(allow()).toBeDisabled();
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(allow()).toBeEnabled();
    expect(screen.queryByText(en.remote.allowWait)).toBeNull();
  });

  it("is enabled at once for an ordinary run", () => {
    show(run());
    expect(allow()).toBeEnabled();
    expect(screen.queryByText(en.remote.allowWait)).toBeNull();
  });

  it("is disabled for a run without a hash", () => {
    show(run({ hash: null }));
    expect(allow()).toBeDisabled();
  });

  it("is disabled once the run has expired, and the card says time is up", () => {
    show(run({ expiresAt: iso(-1000) }));
    expect(allow()).toBeDisabled();
    expect(screen.getByText(en.remote.timeUp)).toBeInTheDocument();
  });

  it("shows the time left as minutes and seconds", () => {
    show(run({ expiresAt: iso(4 * 60_000 + 30_000) }));
    expect(screen.getByText(/^[34]:\d{2} left$/)).toBeInTheDocument();
  });

  it("counts down on its own and disables Allow when the time runs out", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "Date"] });
    show(run({ expiresAt: iso(2500) }));
    expect(screen.getByText("0:03 left")).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(1000));
    expect(screen.getByText("0:02 left")).toBeInTheDocument();
    expect(allow()).toBeEnabled();
    await act(() => vi.advanceTimersByTimeAsync(2000));
    expect(screen.getByText(en.remote.timeUp)).toBeInTheDocument();
    expect(allow()).toBeDisabled();
  });
});

describe("deciding", () => {
  it("posts exactly the run's hash on Allow, nothing else, and reports the change", async () => {
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    const onChanged = show(run({ hash: "abc123" }));
    await userEvent.click(allow());
    await vi.waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls).toHaveLength(1);
    expect(body(calls, "/runs/r1/approve")).toEqual({ hash: "abc123" });
    expect(body(calls, "/runs/r1/approve")).not.toHaveProperty("code");
  });

  it("asks for a code when the API says mfa_required, then retries with hash and code", async () => {
    let first = true;
    const calls = mockApi({
      "POST /runs/r1/approve": () => {
        if (first) {
          first = false;
          return { status: 403, body: { title: "mfa_required" } };
        }
        return { status: 204 };
      },
    });
    const onChanged = show(run({ hash: "h9" }));
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
    await userEvent.click(allow());
    const field = await screen.findByLabelText(en.remote.codeLabel);
    expect(onChanged).not.toHaveBeenCalled();
    expect(screen.queryByText(en.errors.invalid_code)).toBeNull();
    await userEvent.type(field, " 123456 ");
    await userEvent.click(allow());
    await vi.waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => c.body)).toEqual([{ hash: "h9" }, { hash: "h9", code: "123456" }]);
  });

  it("reports a wrong code and keeps the field", async () => {
    mockApi({ "POST /runs/r1/approve": { status: 403, body: { title: "mfa_required" } } });
    const onChanged = show(run());
    await userEvent.click(allow());
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "000000");
    await userEvent.click(allow());
    expect(await screen.findByText(en.errors.invalid_code)).toBeInTheDocument();
    expect(screen.getByLabelText(en.remote.codeLabel)).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("shows another failure as it is (the run changed) and does not report a change", async () => {
    mockApi({ "POST /runs/r1/approve": { status: 409, body: { title: "run_mismatch" } } });
    const onChanged = show(run());
    await userEvent.click(allow());
    expect(await screen.findByText(en.errors.run_mismatch)).toBeInTheDocument();
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("denies with the optional reason, trimmed", async () => {
    const calls = mockApi({ "POST /runs/r1/deny": { status: 204 } });
    const onChanged = show(run());
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    expect(screen.queryByRole("button", { name: "Allow" })).toBeNull();
    await userEvent.type(screen.getByLabelText(en.remote.denyReason), "  not now  ");
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    await vi.waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(body(calls, "/runs/r1/deny")).toEqual({ reason: "not now" });
  });

  it("denies without a reason by sending none", async () => {
    const calls = mockApi({ "POST /runs/r1/deny": { status: 204 } });
    show(run());
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    await userEvent.type(screen.getByLabelText(en.remote.denyReason), "   ");
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    await vi.waitFor(() => expect(calls).toHaveLength(1));
    expect(body(calls, "/runs/r1/deny")).toEqual({});
  });

  it("can back out of denying and shows a failed denial", async () => {
    mockApi({ "POST /runs/r1/deny": { status: 409, body: { title: "not_pending" } } });
    const onChanged = show(run());
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(allow()).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    await userEvent.click(screen.getByRole("button", { name: "Deny" }));
    expect(await screen.findByText(en.errors.not_pending)).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });
});
