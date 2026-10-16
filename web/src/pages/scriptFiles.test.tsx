import { act, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebRunView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { renderPlain, run } from "../test/remote-fixtures";
import { RunApproval } from "./RunApproval";

const script = (over: Partial<WebRunView> = {}): WebRunView => run({
  mode: "script", argv: null, cwd: null, interpreter: true, scriptInterpreter: "python", scriptBody: "import helper\nprint(helper.VALUE)\n", scriptArgs: [],
  interpreterPath: "/usr/bin/python3", targetEnvironment: "test", targetFunction: "application", strict: false, needsCode: false, hash: "h",
  scriptFiles: [{ path: "helper.py", text: "VALUE = 42\n" }, { path: "data/in.txt", text: "one\ntwo\n" }], scriptStdin: "typed\n", ...over,
});

const show = (r: WebRunView) => renderPlain(<RunApproval run={r} ws="w1" onChanged={vi.fn()} />);
const allow = () => screen.getByRole("button", { name: en.remote.allow });

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("the files and the stdin of a script on the approval card", () => {
  it("lists every file with its path and size and the stdin text, each in its own numbered box", () => {
    show(script());
    expect(screen.getByText(en.remote.scriptExtras.replace("{n}", "2"))).toBeInTheDocument();
    const helper = screen.getByRole("region", { name: en.remote.scriptFile.replace("{path}", "helper.py") });
    expect(within(helper).getByText("VALUE = 42")).toBeInTheDocument();
    const data = screen.getByRole("region", { name: en.remote.scriptFile.replace("{path}", "data/in.txt") });
    expect(data).toHaveTextContent("1one");
    expect(data).toHaveTextContent("2two");
    expect(screen.getByRole("region", { name: en.remote.scriptStdin })).toHaveTextContent("typed");
    expect(screen.getByText(en.remote.scriptChars.replace("{n}", "11"), { exact: false })).toBeInTheDocument();
  });

  it("shows nothing extra for a script without files or stdin", () => {
    show(script({ scriptFiles: null, scriptStdin: null }));
    expect(screen.queryByText(/This script carries/)).toBeNull();
    expect(screen.queryByRole("region", { name: en.remote.scriptStdin })).toBeNull();
  });

  it("makes hidden characters in a file name and in its text visible", () => {
    show(script({ scriptFiles: [{ path: "a‮b.txt", text: "x​y\n" }], scriptStdin: null }));
    expect(document.querySelector("mark.vis-flag")).not.toBeNull();
  });

  it("allows only after the script AND every file AND the stdin were scrolled to their ends", async () => {
    vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(1000);
    vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(100);
    show(script());
    await act(async () => {
      vi.advanceTimersByTime(config.runAllowDelayMs + 100);
    });
    expect(allow()).toBeDisabled();

    const atBottom = (box: HTMLElement) => Object.defineProperty(box, "scrollTop", { configurable: true, value: 900 }); // this box only
    const names = [en.remote.scriptListing, en.remote.scriptFile.replace("{path}", "helper.py"), en.remote.scriptFile.replace("{path}", "data/in.txt")];
    for (const name of names) {
      const box = screen.getByRole("region", { name });
      atBottom(box);
      fireEvent.scroll(box);
      expect(allow()).toBeDisabled(); // one of the boxes is still unread
    }

    const stdin = screen.getByRole("region", { name: en.remote.scriptStdin });
    atBottom(stdin);
    fireEvent.scroll(stdin);
    expect(await screen.findByRole("button", { name: en.remote.allow })).toBeEnabled();
  });

  it("a script with files that all fit needs no scrolling at all", async () => {
    show(script());
    await act(async () => {
      vi.advanceTimersByTime(config.runAllowDelayMs + 100);
    });
    expect(allow()).toBeEnabled();
  });

  it("hides the files and the stdin from someone who may not see the script", () => {
    show(script({ visible: false, scriptBody: null, scriptFiles: null, scriptStdin: null }));
    expect(screen.getByText(en.remote.commandHidden)).toBeInTheDocument();
    expect(screen.queryByText("VALUE = 42")).toBeNull();
  });
});
