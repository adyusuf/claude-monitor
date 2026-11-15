import { screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { MachineAccessPolicyView, MachineAccessView } from "../api/typesAccess";
import { renderPlain } from "../test/remote-fixtures";
import { MachineAccess } from "./MachineAccess";

const policy = (over: Partial<MachineAccessPolicyView> = {}): MachineAccessPolicyView => ({
  projectId: "p7", projectName: "Widgets", webVersion: 3, appliedVersion: 3, state: "applied", isSigned: true, allowedFolders: ["/srv/widgets"],
  deniedFolders: [], applications: [], includeProjectFolder: false, shell: false, ...over,
});

const access = (over: Partial<MachineAccessView> = {}): MachineAccessView => ({
  ceiling: null, policies: [], reportedAt: new Date(2026, 9, 5, 9, 30).toISOString(), defaultApplies: false, ...over,
});

const show = (view: MachineAccessView) => renderPlain(<MachineAccess ws="w1" access={view} />);
const row = (name: string) => screen.getByRole("link", { name }).closest("li") as HTMLElement;

describe("a machine's access card", () => {
  it("says the agent is too old and shows nothing else when it sent no access report", () => {
    show(access({ reportedAt: null, policies: [policy()], ceiling: { allowedFolders: ["/x"], deniedFolders: [], applications: [], shell: true } }));
    expect(screen.getByRole("status")).toHaveTextContent("This machine's agent is too old to apply access policies");
    expect(screen.queryByText("Project policies")).toBeNull();
    expect(screen.queryByText("This machine's own limit")).toBeNull();
  });

  it("states when the machine reported", () => {
    show(access());
    expect(screen.getByText(/Reported 05\/10\/2026 09:30\./)).toBeInTheDocument();
  });

  it("says whether a run without a policy gets the default here", () => {
    const { unmount } = show(access({ defaultApplies: true }));
    expect(screen.getByText(/A run without a policy gets the default here/)).toBeInTheDocument();
    unmount();
    show(access({ defaultApplies: false }));
    expect(screen.getByText("A run without a policy is limited only by this machine's own settings.")).toBeInTheDocument();
  });

  it("shows the machine's own limit with its folders, applications and shell when the API sent it", () => {
    show(access({ ceiling: { allowedFolders: ["/srv"], deniedFolders: ["/srv/secret"], applications: [{ path: "/usr/bin/make", sha256: null }], shell: true } }));
    const ceiling = screen.getByRole("region", { name: "This machine's own limit" });
    expect(within(ceiling).getByRole("list", { name: "Allowed folders" })).toHaveTextContent("/srv");
    expect(within(ceiling).getByRole("list", { name: "Denied folders" })).toHaveTextContent("/srv/secret");
    expect(within(ceiling).getByRole("list", { name: "Applications" })).toHaveTextContent("/usr/bin/make");
    expect(within(ceiling).getByText("Shell and script runs: allowed by this machine.")).toBeInTheDocument();
    expect(screen.queryByText(/The machine's own limit is shown to its owner/)).toBeNull();
  });

  it("says the shell is not allowed when the machine's own limit has it off", () => {
    show(access({ ceiling: { allowedFolders: [], deniedFolders: [], applications: [], shell: false } }));
    expect(screen.getByText("Shell and script runs: not allowed by this machine.")).toBeInTheDocument();
  });

  it("hides the machine's own limit and says who sees it when the API left it out", () => {
    show(access({ ceiling: null }));
    expect(screen.getByText("The machine's own limit is shown to its owner and the workspace's admins.")).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "This machine's own limit" })).toBeNull();
  });

  it("says no project policy covers the machine when there is none", () => {
    show(access({ policies: [] }));
    expect(screen.getByText("No project policy covers a repository of this machine.")).toBeInTheDocument();
  });
});

describe("a project policy on a machine", () => {
  it("links the project and shows the web version, the version applied here, the state and that it is signed", () => {
    show(access({ policies: [policy()] }));
    const li = row("Widgets");
    expect(within(li).getByRole("link", { name: "Widgets" })).toHaveAttribute("href", "/w/w1/projects/p7");
    expect(within(li).getByText("Web: version 3")).toBeInTheDocument();
    expect(within(li).getByText("Here: version 3")).toBeInTheDocument();
    expect(within(li).getByText("Here: version 3")).not.toHaveClass("warn");
    expect(within(li).getByText("applied")).toHaveClass("ok");
    expect(within(li).getByText("Signed")).toHaveClass("ok");
  });

  it("marks the applied version as behind when it is older than the web's", () => {
    show(access({ policies: [policy({ webVersion: 5, appliedVersion: 4 })] }));
    expect(within(row("Widgets")).getByText("Here: version 4")).toHaveClass("warn");
  });

  it("says nothing was applied here yet and that the policy was not received when the machine has no state", () => {
    show(access({ policies: [policy({ appliedVersion: null, state: null })] }));
    const li = row("Widgets");
    expect(within(li).getByText("Here: none")).not.toHaveClass("warn");
    expect(within(li).getByText("not received yet")).toHaveClass("warn");
  });

  it("shows a state in which the default applies as a warning, with its own words", () => {
    show(access({ policies: [policy({ state: "unsigned_ignored" })] }));
    expect(within(row("Widgets")).getByText("not signed: ignored")).toHaveClass("warn");
  });

  it("shows the state's own name as it came when the web does not know it", () => {
    show(access({ policies: [policy({ state: "from_the_future" })] }));
    expect(within(row("Widgets")).getByText("from_the_future")).toHaveClass("warn");
  });

  it("shows an unsigned policy as not signed and a policy that allows shell runs with a shell badge", () => {
    show(access({ policies: [policy({ isSigned: false, shell: true })] }));
    const li = row("Widgets");
    expect(within(li).getByText("Not signed")).toHaveClass("warn");
    expect(within(li).getByText("shell allowed")).toHaveClass("status-failed");
  });

  it("shows no shell badge for a policy that does not allow shell runs", () => {
    show(access({ policies: [policy({ shell: false })] }));
    expect(within(row("Widgets")).queryByText("shell allowed")).toBeNull();
  });

  it("lists the policy's effective folders and applications", () => {
    show(access({ policies: [policy({ allowedFolders: ["/srv/widgets"], deniedFolders: ["/srv/widgets/keys"], applications: [{ path: "/usr/bin/git", sha256: "ab12" }] })] }));
    const li = row("Widgets");
    expect(within(li).getByRole("list", { name: "Allowed folders" })).toHaveTextContent("/srv/widgets");
    expect(within(li).getByRole("list", { name: "Denied folders" })).toHaveTextContent("/srv/widgets/keys");
    expect(within(li).getByRole("list", { name: "Applications" })).toHaveTextContent("/usr/bin/git");
    expect(within(li).getByText("ab12")).toBeInTheDocument();
  });

  it("says the project's own folder comes on top of the listed folders when the machine's own limit lists no folder", () => {
    show(access({
      ceiling: { allowedFolders: [], deniedFolders: [], applications: [], shell: true },
      policies: [policy({ includeProjectFolder: true })],
    }));
    expect(within(row("Widgets")).getByText("Plus the project's own folder.")).toBeInTheDocument();
  });

  it("does not promise the project's own folder when the machine's own limit allows only some folders", () => {
    show(access({
      ceiling: { allowedFolders: ["/srv"], deniedFolders: [], applications: [], shell: true },
      policies: [policy({ includeProjectFolder: true })],
    }));
    expect(within(row("Widgets")).queryByText("Plus the project's own folder.")).toBeNull();
  });
});
