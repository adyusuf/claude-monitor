import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { approvalKeys } from "../lib/approvalKey";
import { en } from "../i18n/en";
import { defaultView, policyDocument, policyView, REPO, SHA } from "../test/access-fixtures";
import { mockApi } from "../test/helpers";
import { renderPlain } from "../test/remote-fixtures";
import { ProjectAccessCard } from "./ProjectAccessCard";

beforeEach(async () => approvalKeys.forget());
afterEach(async () => approvalKeys.forget());

const rules = { allowedFolders: ["/srv/app"], deniedFolders: ["/srv/app/secret"], applications: [{ path: "/usr/bin/make", sha256: SHA }] };
const show = () => renderPlain(<ProjectAccessCard ws="w1" projectId="p1" />);
const edit = () => userEvent.click(screen.getByRole("button", { name: en.access.edit }));
const policyReads = (calls: { method: string; path: string }[]) => calls.filter((c) => c.method === "GET" && c.path === "/projects/p1/access-policy");

describe("the project's access policy card: what it shows", () => {
  it("is left out entirely when the API has no access policies (404)", async () => {
    const calls = mockApi({ "GET /projects/p1/access-policy": { status: 404 } });
    const { container } = show();
    await waitFor(() => expect(container).toBeEmptyDOMElement());
    expect(policyReads(calls)).toHaveLength(1);
  });

  it("shows the error, no spinner and no edit button when the policy cannot be read", async () => {
    mockApi({ "GET /projects/p1/access-policy": { status: 500 } });
    show();
    expect(await screen.findByText(en.errors.server_error)).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: en.common.loading })).toBeNull();
    expect(screen.queryByRole("button", { name: en.access.edit })).toBeNull();
  });

  it("says no policy is saved and what the default is for a project without one", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: defaultView() } });
    show();
    expect(await screen.findByText(en.access.none)).toBeInTheDocument();
    expect(screen.getByText(en.access.defaultHint)).toBeInTheDocument();
    expect(screen.queryByText(en.access.keys)).toBeNull();
  });

  it("shows a signed policy's version, signer, repositories, project folder, shell and per-system lists", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ document: policyDocument({ linux: rules }) }) } });
    show();
    expect(await screen.findByText("Version 3")).toBeInTheDocument();
    expect(screen.getByText("Signed by Örnek Kişi")).toHaveClass("badge", "ok");
    expect(screen.getByText(REPO)).toBeInTheDocument();
    expect(screen.getByText(en.access.allowed)).toBeInTheDocument();
    expect(screen.getByText(en.access.shellOff)).toBeInTheDocument();
    const linux = within(screen.getByRole("region", { name: en.access.os.linux }));
    expect(within(linux.getByRole("list", { name: en.access.allowedFolders })).getByText("/srv/app")).toBeInTheDocument();
    expect(linux.getByText("/srv/app/secret")).toBeInTheDocument();
    expect(linux.getByText(SHA)).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: en.access.os.macos })).getByText(en.access.onlyProjectFolder)).toBeInTheDocument();
  });

  it("says Signed when the signer is not known", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ signerName: null }) } });
    show();
    expect(await screen.findByText(en.access.signed)).toHaveClass("badge", "ok");
  });

  it("marks an unsigned version as such", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ isSigned: false, signerName: null }) } });
    show();
    expect(await screen.findByText(en.access.unsigned)).toHaveClass("badge", "warn");
  });

  it("flags that shell runs are allowed", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ document: policyDocument({ shell: true }) }) } });
    show();
    expect(await screen.findByText(en.access.shellOn)).toHaveClass("status-failed");
  });

  it("says a taken-back policy was taken back, shows the default hint and no lists", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ document: policyDocument({ cleared: true, includeProjectFolder: false }) }) } });
    show();
    expect(await screen.findByText(en.access.cleared)).toBeInTheDocument();
    expect(screen.getByText(en.access.defaultHint)).toBeInTheDocument();
    expect(screen.queryByText(en.access.keys)).toBeNull();
  });

  it("warns about repositories the machines spell differently, naming them", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ nearKeys: ["Git.Example.org/Shop"] }) } });
    show();
    expect(await screen.findByText(en.access.nearKeys)).toBeInTheDocument();
    expect(screen.getByText("Git.Example.org/Shop")).toBeInTheDocument();
  });

  it("shows no spelling warning when no repository differs", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ nearKeys: [] }) } });
    show();
    await screen.findByText("Version 3");
    expect(screen.queryByText(en.access.nearKeys)).toBeNull();
  });
});

describe("the project's access policy card: editing", () => {
  it("offers Edit only to someone who may edit", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView({ canEdit: false }) } });
    show();
    await screen.findByText("Version 3");
    expect(screen.queryByRole("button", { name: en.access.edit })).toBeNull();
  });

  it("swaps the facts for the form on Edit and back on Cancel", async () => {
    mockApi({ "GET /projects/p1/access-policy": { body: policyView() } });
    show();
    await screen.findByText("Version 3");
    await edit();
    expect(screen.getByRole("button", { name: en.access.save })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.access.edit })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: en.common.cancel }));
    expect(screen.queryByRole("button", { name: en.access.save })).toBeNull();
    expect(screen.getByRole("button", { name: en.access.edit })).toBeInTheDocument();
  });

  it("reads the policy again after a save and says it was saved", async () => {
    let saved = false;
    const calls = mockApi({
      "GET /projects/p1/access-policy": () => ({ body: policyView({ version: saved ? 4 : 3 }) }),
      "PUT /projects/p1/access-policy": () => {
        saved = true;
        return { body: policyView({ version: 4 }) };
      },
    });
    show();
    await screen.findByText("Version 3");
    await edit();
    await userEvent.click(screen.getByRole("button", { name: en.access.save }));
    expect(await screen.findByText(en.access.saved)).toBeInTheDocument();
    expect(await screen.findByText("Version 4")).toBeInTheDocument();
    expect(policyReads(calls)).toHaveLength(2);
    expect(screen.queryByRole("button", { name: en.access.save })).toBeNull();
  });

  it("says it was saved and signed when this browser held a key", async () => {
    await approvalKeys.generate("Mine");
    mockApi({
      "GET /projects/p1/access-policy": { body: policyView() },
      "PUT /projects/p1/access-policy": { body: policyView({ version: 4 }) },
    });
    show();
    await screen.findByText("Version 3");
    await edit();
    await screen.findByText(en.access.willSign);
    await userEvent.click(screen.getByRole("button", { name: en.access.save }));
    expect(await screen.findByText(en.access.savedSigned)).toBeInTheDocument();
  });

  it("shows the newer version and tells the person to make the change again when another save came first", async () => {
    const calls = mockApi({
      "GET /projects/p1/access-policy": { body: policyView() },
      "PUT /projects/p1/access-policy": { status: 409, body: { title: "access_policy_changed" } },
    });
    show();
    await screen.findByText("Version 3");
    await edit();
    await userEvent.click(screen.getByRole("button", { name: en.access.save }));
    expect(await screen.findByText(en.access.reloaded)).toBeInTheDocument();
    expect(screen.queryByText(en.access.saved)).toBeNull();
    expect(screen.queryByRole("button", { name: en.access.save })).toBeNull();
    expect(policyReads(calls)).toHaveLength(2);
  });
});
