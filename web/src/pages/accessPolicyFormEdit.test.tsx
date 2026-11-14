import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AccessOsRules, AccessPolicyDocument, AccessPolicyView } from "../api/typesAccess";
import { en } from "../i18n/en";
import { approvalKeys } from "../lib/approvalKey";
import { defaultView, policyDocument, policyView, REPO, SHA } from "../test/access-fixtures";
import { mockApi } from "../test/helpers";
import { renderPlain } from "../test/remote-fixtures";
import { AccessPolicyForm } from "./AccessPolicyForm";

beforeEach(async () => approvalKeys.forget());
afterEach(async () => approvalKeys.forget());

const saved = (c: { method: string }) => c.method === "PUT";
const empty: AccessOsRules = { allowedFolders: [], deniedFolders: [], applications: [] };

function open(view: AccessPolicyView = policyView()) {
  const onSaved = vi.fn();
  const calls = mockApi({ "PUT /projects/p1/access-policy": { body: policyView({ version: 4 }) } });
  renderPlain(<AccessPolicyForm view={view} ws="w1" onSaved={onSaved} onConflict={() => undefined} onCancel={() => undefined} />);
  return { onSaved, calls };
}
const save = () => userEvent.click(screen.getByRole("button", { name: en.access.save }));
const sent = async (calls: { method: string; body: unknown }[], onSaved: ReturnType<typeof vi.fn>) => {
  await save();
  await waitFor(() => expect(onSaved).toHaveBeenCalled());
  return (calls.find(saved)!.body as { document: AccessPolicyDocument }).document;
};
const addAllowed = () => userEvent.click(screen.getByRole("button", { name: en.access.addAllowed }));
const allowedRow = (n: number) => screen.getByLabelText(`${en.access.allowedFolders} ${n}`);

describe("the access policy form: folders and applications", () => {
  it("sends a typed folder in canonical form, without the trailing separator", async () => {
    const { calls, onSaved } = open();
    await addAllowed();
    await userEvent.type(allowedRow(1), "/srv/app/");
    expect((await sent(calls, onSaved)).linux).toEqual({ ...empty, allowedFolders: ["/srv/app"] });
  });

  it("leaves out a row the person added and did not fill in", async () => {
    const { calls, onSaved } = open();
    await addAllowed();
    await userEvent.click(screen.getByRole("button", { name: en.access.addDenied }));
    expect((await sent(calls, onSaved)).linux).toEqual(empty);
  });

  it("removes a row with its Remove button", async () => {
    const { calls, onSaved } = open(policyView({ document: policyDocument({ linux: { ...empty, allowedFolders: ["/srv/app", "/srv/data"] } }) }));
    await userEvent.click(screen.getByRole("button", { name: `${en.access.remove} ${en.access.allowedFolders} 1` }));
    expect(allowedRow(1)).toHaveValue("/srv/data");
    expect((await sent(calls, onSaved)).linux.allowedFolders).toEqual(["/srv/data"]);
  });

  it("sends a denied folder", async () => {
    const { calls, onSaved } = open();
    await userEvent.click(screen.getByRole("button", { name: en.access.addDenied }));
    await userEvent.type(screen.getByLabelText(`${en.access.deniedFolders} 1`), "/srv/app/secret");
    expect((await sent(calls, onSaved)).linux.deniedFolders).toEqual(["/srv/app/secret"]);
  });

  it("sends an application with its hash in lower case", async () => {
    const { calls, onSaved } = open();
    await userEvent.click(screen.getByRole("button", { name: en.access.addApplication }));
    await userEvent.type(screen.getByLabelText(`${en.access.applications} 1`), "/usr/bin/make");
    await userEvent.type(screen.getByLabelText(`${en.access.sha256} 1`), SHA.toUpperCase());
    expect((await sent(calls, onSaved)).linux.applications).toEqual([{ path: "/usr/bin/make", sha256: SHA }]);
  });

  it("marks a folder that is not a full path and holds Save back, sending nothing", async () => {
    const { calls, onSaved } = open();
    await addAllowed();
    await userEvent.type(allowedRow(1), "relative/dir");
    expect(allowedRow(1)).toHaveAttribute("aria-invalid", "true");
    await save();
    expect(await screen.findAllByText(en.errors.policy_path)).toHaveLength(2); // beside the row, and as the reason the save did not go out
    expect(calls.some(saved)).toBe(false);
    expect(onSaved).not.toHaveBeenCalled();
  });

  it("marks a hash that is not 64 hexadecimal characters", async () => {
    const { calls } = open();
    await userEvent.click(screen.getByRole("button", { name: en.access.addApplication }));
    await userEvent.type(screen.getByLabelText(`${en.access.applications} 1`), "/usr/bin/make");
    await userEvent.type(screen.getByLabelText(`${en.access.sha256} 1`), "not-a-hash");
    await save();
    expect((await screen.findAllByText(en.errors.policy_sha256)).length).toBeGreaterThan(0);
    expect(calls.some(saved)).toBe(false);
  });

  it("warns beside the row and on the form when an application is a shell or an interpreter", async () => {
    open();
    await userEvent.click(screen.getByRole("button", { name: en.access.addApplication }));
    await userEvent.type(screen.getByLabelText(`${en.access.applications} 1`), "/bin/bash");
    expect(screen.getByText(en.access.interpreterRow)).toBeInTheDocument();
    expect(screen.getByText(en.access.interpreterWarning)).toBeInTheDocument();
  });

  it("keeps what was typed for one system when the person looks at another and comes back", async () => {
    open();
    await addAllowed();
    await userEvent.type(allowedRow(1), "/srv/app");
    await userEvent.click(screen.getByRole("tab", { name: en.access.os.macos }));
    expect(screen.queryByLabelText(`${en.access.allowedFolders} 1`)).toBeNull();
    await userEvent.click(screen.getByRole("tab", { name: en.access.os.linux }));
    expect(allowedRow(1)).toHaveValue("/srv/app");
  });

  it("sends a folder typed under Windows in Windows form, in the Windows section only", async () => {
    const { calls, onSaved } = open();
    await userEvent.click(screen.getByRole("tab", { name: en.access.os.windows }));
    await addAllowed();
    await userEvent.type(allowedRow(1), "c:/src/app/");
    const document = await sent(calls, onSaved);
    expect(document.windows.allowedFolders).toEqual(["C:\\src\\app"]);
    expect(document.linux).toEqual(empty);
  });
});

describe("the access policy form: shell, project folder and taking back", () => {
  it("warns when shell is switched on and sends it", async () => {
    const { calls, onSaved } = open();
    await userEvent.click(screen.getByRole("switch", { name: new RegExp(en.access.shell) }));
    expect(screen.getByText(en.access.shellWarning)).toBeInTheDocument();
    expect((await sent(calls, onSaved)).shell).toBe(true);
  });

  it("sends the project folder as unticked when the person unticks it", async () => {
    const { calls, onSaved } = open();
    await userEvent.click(screen.getByRole("checkbox", { name: new RegExp(en.access.projectFolder) }));
    expect((await sent(calls, onSaved)).includeProjectFolder).toBe(false);
  });

  it("starts an edit of a taken-back policy with the project folder allowed again and no longer cleared", async () => {
    const { calls, onSaved } = open(policyView({ document: policyDocument({ cleared: true, includeProjectFolder: false }) }));
    expect(screen.getByRole("checkbox", { name: new RegExp(en.access.projectFolder) })).toBeChecked();
    expect(screen.queryByRole("button", { name: en.access.clear })).toBeNull();
    const document = await sent(calls, onSaved);
    expect(document).toMatchObject({ cleared: false, includeProjectFolder: true, projectKeys: [REPO] });
  });

  it("offers no way to take back a policy that was never saved", () => {
    open(defaultView());
    expect(screen.queryByRole("button", { name: en.access.clear })).toBeNull();
  });

  it("sends nothing when the person does not confirm taking the policy back", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const { calls } = open();
    await userEvent.click(screen.getByRole("button", { name: en.access.clear }));
    expect(window.confirm).toHaveBeenCalledWith(en.access.clearConfirm);
    expect(calls.some(saved)).toBe(false);
  });

  it("sends a cleared document with the same repositories and nothing else when the person confirms", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const { calls, onSaved } = open(policyView({ document: policyDocument({ shell: true, linux: { ...empty, allowedFolders: ["/srv/app"] } }) }));
    await userEvent.click(screen.getByRole("button", { name: en.access.clear }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect((calls.find(saved)!.body as { document: AccessPolicyDocument }).document).toEqual(policyDocument({ includeProjectFolder: false, cleared: true }));
  });

  it("keeps the tab strip of operating systems named for assistive technology", () => {
    open();
    expect(within(screen.getByRole("tablist", { name: en.access.osTabs })).getAllByRole("tab").map((t) => t.textContent)).toEqual(["Linux", "macOS", "Windows"]);
  });
});
