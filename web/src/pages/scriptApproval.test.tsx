import { act, fireEvent, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { mockApi } from "../test/helpers";
import { renderPlain, run } from "../test/remote-fixtures";
import { RunApproval } from "./RunApproval";
import { RunOutputAccount } from "./RunOutputAccount";
import { scriptLines } from "./ScriptListing";

const script = (over: Partial<WebRunView> = {}): WebRunView => run({
  mode: "script", argv: null, cwd: null, interpreter: true, scriptInterpreter: "sh", scriptBody: "echo one\necho two\n", scriptArgs: ["a b", ""],
  interpreterPath: "/bin/sh", targetEnvironment: "test", targetFunction: "application", strict: false, needsCode: false, hash: "script-hash", ...over,
});

const show = (r: WebRunView) => renderPlain(<RunApproval run={r} ws="w1" onChanged={vi.fn()} />);
const allow = () => screen.getByRole("button", { name: en.remote.allow });

/** A box taller than what it shows, so the script has to be scrolled; jsdom has no layout. */
function tall() {
  vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(1000);
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(100);
}

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("the script card", () => {
  it("numbers every line of the script, shows the interpreter and where it lives, and puts every argument in its own box", () => {
    show(script());
    const region = screen.getByRole("region", { name: en.remote.scriptListing });
    expect(within(region).getAllByRole("listitem")).toHaveLength(2);
    expect(region).toHaveTextContent("1echo one");
    expect(region).toHaveTextContent("2echo two");
    expect(screen.getByText("sh")).toBeInTheDocument();
    expect(screen.getByText("(/bin/sh)")).toBeInTheDocument();
    const boxes = within(screen.getByRole("list", { name: en.remote.scriptArgs })).getAllByRole("listitem");
    expect(boxes).toHaveLength(2);
    expect(boxes[0]).toHaveTextContent("a b");
    expect(boxes[1]).toHaveTextContent(en.remote.emptyArg);
  });

  it("makes tabs, escapes and non-ASCII characters in the script visible and never shortens a long line", () => {
    const long = "x".repeat(900);
    show(script({ scriptBody: `echo ok‮\n${long}\n` }));
    const region = screen.getByRole("region", { name: en.remote.scriptListing });
    expect(region).toHaveTextContent("echo ok\\u202E");
    expect(region.querySelector("mark.vis-flag")).not.toBeNull();
    expect(region.textContent).toContain(long);
  });

  it("does not make an empty last line out of a final line break", () => {
    expect(scriptLines("a\nb\n")).toEqual(["a", "b"]);
    expect(scriptLines("a\nb")).toEqual(["a", "b"]);
    expect(scriptLines("")).toEqual([""]);
    expect(scriptLines("\n")).toEqual([""]);
  });

  it("shows the red banner always and names PRODUCTION or DATABASE on a strict machine", () => {
    show(script());
    expect(screen.getByRole("alert")).toHaveTextContent(en.remote.scriptBanner);
  });

  it("names the strict machine in the banner and shows its role in red", () => {
    show(script({ strict: true, needsCode: true, targetEnvironment: "production", targetFunction: "database" }));
    expect(screen.getByRole("alert")).toHaveTextContent(`${en.remote.scriptDatabase} MACHINE`);
    expect(screen.getByText("production/database")).toHaveClass("status-failed");
    const production = script({ strict: true, needsCode: true, targetEnvironment: "production", targetFunction: "application" });
    expect(production.targetFunction).toBe("application");
  });

  it("hides the script from someone who may not see it", () => {
    show(script({ visible: false, scriptBody: null, scriptArgs: null }));
    expect(screen.getByText(en.remote.commandHidden)).toBeInTheDocument();
  });
});

describe("allowing a script", () => {
  it("waits until the script was scrolled to its end and then three more seconds", async () => {
    tall();
    show(script());
    expect(allow()).toBeDisabled();
    expect(screen.getByText(en.remote.scriptScroll)).toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(config.runAllowDelayMs + 100);
    });
    expect(allow()).toBeDisabled(); // the time passed, the script was not read

    const region = screen.getByRole("region", { name: en.remote.scriptListing });
    vi.spyOn(HTMLElement.prototype, "scrollTop", "get").mockReturnValue(900);
    fireEvent.scroll(region);
    expect(await screen.findByRole("button", { name: en.remote.allow })).toBeEnabled();
  });

  it("is ready after the delay when the whole script fits without scrolling", async () => {
    show(script());
    await act(async () => {
      vi.advanceTimersByTime(config.runAllowDelayMs + 100);
    });
    expect(allow()).toBeEnabled();
  });

  it("sends the hash of the card and the code the owner typed, and the code field is there from the start on a strict machine", async () => {
    const calls = mockApi({ "POST /runs/r1/approve": { status: 204 } });
    show(script({ strict: true, needsCode: true, targetEnvironment: "production", targetFunction: "application" }));
    expect(screen.getByText(en.remote.scriptNeedsCode)).toBeInTheDocument();
    const code = screen.getByLabelText(en.remote.codeLabel);
    await act(async () => {
      vi.advanceTimersByTime(config.runAllowDelayMs + 100);
    });
    expect(allow()).toBeDisabled(); // a code is needed first
    await userEvent.setup({ advanceTimers: vi.advanceTimersByTime }).type(code, "123456");
    expect(allow()).toBeEnabled();
    await userEvent.setup({ advanceTimers: vi.advanceTimersByTime }).click(allow());
    expect(calls.find((c) => c.path === "/runs/r1/approve")?.body).toEqual({ hash: "script-hash", code: "123456" });
  });

  it("shows no code field on a machine that is not strict", async () => {
    show(script());
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
  });

  it("says why when the API refuses for a missing two-step sign-in or a changed role", async () => {
    mockApi({ "POST /runs/r1/approve": { status: 403, body: { title: "totp_required" } } });
    show(script());
    await act(async () => {
      vi.advanceTimersByTime(config.runAllowDelayMs + 100);
    });
    await userEvent.setup({ advanceTimers: vi.advanceTimersByTime }).click(allow());
    expect(await screen.findByText(en.errors.totp_required)).toBeInTheDocument();
  });
});

describe("the verdict on a script's output", () => {
  const base = { outputBytes: 100, outputReadBytes: null, outputDroppedBytes: null, outputRefusedBytes: null, outputMissingFromSeq: null, outputCap: null, outputTruncated: false, error: null };

  it("shows nothing for a run without a verdict", () => {
    const { container } = renderPlain(<RunOutputAccount run={{ ...base, outputComplete: null }} />);
    expect(container.textContent).toBe("");
  });

  it("says complete with the size", () => {
    renderPlain(<RunOutputAccount run={{ ...base, outputComplete: true, outputReadBytes: 1500 }} />);
    expect(screen.getByText("Output complete, 1500 B.")).toBeInTheDocument();
  });

  it("names the bytes cut on the target and the bytes the workspace limit refused from a chunk on", () => {
    renderPlain(<RunOutputAccount run={{ ...base, outputComplete: false, outputDroppedBytes: 4096, outputRefusedBytes: 180_000, outputMissingFromSeq: 17, outputCap: 1_048_576, outputTruncated: true }} />);
    const line = screen.getByRole("status");
    expect(line).toHaveTextContent("4 KB cut on the target");
    expect(line).toHaveTextContent("from chunk 17 on were refused by the workspace's output limit of 1.0 MB");
  });

  it("names a stop at the read limit", () => {
    renderPlain(<RunOutputAccount run={{ ...base, outputComplete: false, outputTruncated: true, error: "output_limit" }} />);
    expect(screen.getByRole("status")).toHaveTextContent(en.remote.outputReadLimit);
  });
});
