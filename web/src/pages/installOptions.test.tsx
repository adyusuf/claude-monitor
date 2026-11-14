import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { I18nProvider } from "../i18n";
import { startsWith } from "../test/helpers";
import { InstallOptions, installCommands, runAccountNameOk, serverProblem } from "./InstallOptions";

const choice = { mode: "service" as const, exec: "off" as const, server: "https://pod.example.net", runAccount: false, runAccountName: "" };
const linuxPath = "~/.aipod/bin/aipod";

describe("runAccountNameOk", () => {
  it.each([
    ["builder", true],
    ["_runner", true],
    ["a-b_c9", true],
    ["a".repeat(32), true],
    ["a".repeat(33), false],
    ["9runner", false],
    ["has space", false],
    ["", false],
    ["root", false],
    ["Admin", false],
    ["_aipod", false],
  ])("answers %j with %s", (name, expected) => {
    expect(runAccountNameOk(name)).toBe(expected);
  });
});

describe("serverProblem", () => {
  it.each([
    ["service", "https://pod.example.net", null],
    ["service", "http://pod.example.net", "serviceHttps"],
    ["service", "http://localhost:5000", "serviceHttps"],
    ["service", "not a url", "serviceHttps"],
    ["service", "https://pod.example.net/a|b", "serviceHttps"],
    ["service", "https://pod.example.net/a%20b", "serviceHttps"],
    ["standalone", "https://pod.example.net", null],
    ["standalone", "http://localhost:5000", null],
    ["standalone", "http://127.0.0.1:5000", null],
    ["standalone", "http://[::1]:5000", null],
    ["standalone", "http://pod.example.net", "standaloneHttps"],
    ["standalone", "not a url", "standaloneHttps"],
    ["plugin", "not a url", null],
    ["plugin", "http://pod.example.net", null],
  ] as const)("a %s install on %j is %j", (mode, server, expected) => {
    expect(serverProblem(mode, server)).toBe(expected);
  });
});

describe("installCommands", () => {
  it("gives the plugin mode two lines and the chosen remote-run level, whatever the server", () => {
    const answer = installCommands({ ...choice, mode: "plugin", exec: "shell", server: "not a url" });
    expect(answer).toEqual({ lines: [{ text: "aipod install" }, { text: "aipod install --exec shell" }], noWindows: false });
  });

  it("gives a standalone install one line with the server trimmed of trailing slashes", () => {
    const answer = installCommands({ ...choice, mode: "standalone", exec: "argv", server: "https://pod.example.net//" });
    expect(answer).toEqual({ lines: [{ text: "aipod install --standalone --server https://pod.example.net --exec argv" }], noWindows: false });
  });

  it("refuses a standalone install on a plain-http address that is not this machine", () => {
    expect(installCommands({ ...choice, mode: "standalone", server: "http://pod.example.net" })).toEqual({ problem: "standaloneHttps" });
  });

  it("gives a service one line per system, with the agent's full path, and the Windows one included", () => {
    const answer = installCommands(choice);
    expect(answer).toEqual({
      lines: [
        { os: "linux", text: `sudo ${linuxPath} install --service --server https://pod.example.net --exec off` },
        { os: "macos", text: "sudo \"$HOME/Library/Application Support/aipod/bin/aipod\" install --service --server https://pod.example.net --exec off" },
        { os: "windows", text: "& \"$env:USERPROFILE\\.aipod\\bin\\aipod.exe\" install --service --server https://pod.example.net --exec off" },
      ],
      noWindows: false,
    });
  });

  it("drops the Windows line and says so when the service has a run account", () => {
    const answer = installCommands({ ...choice, runAccount: true });
    expect(answer).toMatchObject({ noWindows: true });
    expect((answer as { lines: { os?: string; text: string }[] }).lines.map((l) => l.os)).toEqual(["linux", "macos"]);
    expect((answer as { lines: { text: string }[] }).lines[0]!.text).toBe(`sudo ${linuxPath} install --service --server https://pod.example.net --exec off --run-account`);
  });

  it("names the run account in the command when one is given, trimmed", () => {
    const answer = installCommands({ ...choice, runAccount: true, runAccountName: " builder " });
    expect((answer as { lines: { text: string }[] }).lines[0]!.text).toBe(`sudo ${linuxPath} install --service --server https://pod.example.net --exec off --run-account builder`);
  });

  it.each(["root", "9lives", "has space"])("refuses the run account name %j", (name) => {
    expect(installCommands({ ...choice, runAccount: true, runAccountName: name })).toEqual({ problem: "runAccountInvalid" });
  });

  it("ignores a name typed earlier once the run account is switched off", () => {
    const answer = installCommands({ ...choice, runAccount: false, runAccountName: "root" });
    expect(answer).toMatchObject({ noWindows: false });
    expect((answer as { lines: { text: string }[] }).lines[0]!.text).not.toContain("--run-account");
  });

  it("refuses a service on a plain-http address", () => {
    expect(installCommands({ ...choice, server: "http://pod.example.net" })).toEqual({ problem: "serviceHttps" });
  });
});

describe("the install choices card", () => {
  const show = (server = "https://pod.example.net") => render(<I18nProvider initial="en"><InstallOptions server={server} /></I18nProvider>);

  it("starts on the plugin with remote runs off", () => {
    show();
    expect(screen.getByText("aipod install")).toBeInTheDocument();
    expect(screen.getByText("aipod install --exec off")).toBeInTheDocument();
  });

  it("writes the chosen remote-run level into the command", async () => {
    show();
    await userEvent.selectOptions(screen.getByLabelText(en.installOptions.exec), "argv");
    expect(screen.getByText("aipod install --exec argv")).toBeInTheDocument();
  });

  it("shows the service commands for each system once the service mode is picked", async () => {
    show();
    await userEvent.selectOptions(screen.getByLabelText(startsWith(en.installOptions.mode)), "service");
    expect(screen.getByText(`sudo ${linuxPath} install --service --server https://pod.example.net --exec off`)).toBeInTheDocument();
    expect(screen.getByText(en.installOptions.os.windows)).toBeInTheDocument();
    expect(screen.queryByText(en.installOptions.noWindows)).toBeNull();
  });

  it("explains a refused run account name, then takes a good one and says there is no Windows line", async () => {
    show();
    await userEvent.selectOptions(screen.getByLabelText(startsWith(en.installOptions.mode)), "service");
    await userEvent.click(screen.getByLabelText(en.installOptions.runAccount));
    await userEvent.type(screen.getByLabelText(startsWith(en.installOptions.runAccountName)), "root");
    expect(screen.getByText(en.installOptions.runAccountInvalid)).toBeInTheDocument();
    expect(screen.getByLabelText(startsWith(en.installOptions.runAccountName))).toHaveAttribute("aria-invalid", "true");
    await userEvent.clear(screen.getByLabelText(startsWith(en.installOptions.runAccountName)));
    await userEvent.type(screen.getByLabelText(startsWith(en.installOptions.runAccountName)), "builder");
    expect(screen.queryByText(en.installOptions.runAccountInvalid)).toBeNull();
    expect(screen.getByText(`sudo ${linuxPath} install --service --server https://pod.example.net --exec off --run-account builder`)).toBeInTheDocument();
    expect(screen.getByText(en.installOptions.noWindows)).toBeInTheDocument();
  });

  it("shows the https requirement instead of a command for a service on a plain-http address", async () => {
    show("http://pod.example.net");
    await userEvent.selectOptions(screen.getByLabelText(startsWith(en.installOptions.mode)), "service");
    expect(screen.getByText(en.installOptions.serviceHttps)).toBeInTheDocument();
    expect(screen.queryByText(/install --service/)).toBeNull();
  });

  it("shows the localhost-only requirement for a standalone install on a plain-http address", async () => {
    show("http://pod.example.net");
    await userEvent.selectOptions(screen.getByLabelText(startsWith(en.installOptions.mode)), "standalone");
    expect(screen.getByText(en.installOptions.standaloneHttps)).toBeInTheDocument();
  });
});
