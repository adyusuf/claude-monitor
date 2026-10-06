import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { WebGrantView } from "../api/types";
import { config } from "../config";
import { en } from "../i18n/en";
import { mockApi } from "../test/helpers";
import { grant, job, renderPlain } from "../test/remote-fixtures";
import { GrantList } from "./GrantList";
import { JobList } from "./JobList";

const grants = (list: WebGrantView[], canCreate = false, onChanged = vi.fn()) => {
  renderPlain(<GrantList grants={list} agentId="a1" canCreate={canCreate} onChanged={onChanged} />);
  return onChanged;
};
const btn = (name: string) => screen.queryByRole("button", { name });

describe("the grant list", () => {
  it("says there are no grants, and shows a grant's template boxed in full with who holds it", () => {
    const { unmount } = renderPlain(<GrantList grants={[]} agentId="a1" canCreate={false} onChanged={vi.fn()} />);
    expect(screen.getByText(en.remote.noGrants)).toBeInTheDocument();
    unmount();
    grants([grant({ template: ["/usr/bin/ls", "-la", "a‮b"], requestedByHostname: "laptop-9", useCount: 3, reason: "needed" })]);
    const boxes = within(screen.getByRole("list", { name: en.remote.argvList })).getAllByRole("listitem");
    expect(boxes).toHaveLength(3);
    expect(boxes[2]).toHaveTextContent("a\\u202Eb");
    expect(screen.getByText("Diğer Kişi")).toBeInTheDocument();
    expect(screen.getByText("asked from laptop-9")).toBeInTheDocument();
    expect(screen.getByText(/used 3 times/)).toBeInTheDocument();
    expect(screen.getByText(en.remote.reasonUntrusted)).toBeInTheDocument();
    expect(screen.getByText(en.remote.grantStatus.active)).toBeInTheDocument();
  });

  it("gives the owner approve and deny for a requested grant, and no revoke", () => {
    grants([grant({ status: "requested", canDecide: true, canRevoke: false })]);
    expect(btn("Approve")).toBeInTheDocument();
    expect(btn("Deny")).toBeInTheDocument();
    expect(btn("Revoke")).toBeNull();
  });

  it("gives whoever may revoke a revoke button and nothing else", () => {
    grants([grant({ canDecide: false, canRevoke: true })]);
    expect(btn("Revoke")).toBeInTheDocument();
    expect(btn("Approve")).toBeNull();
    expect(btn("Deny")).toBeNull();
  });

  it("gives anyone else no buttons at all", () => {
    grants([grant({ status: "requested", canDecide: false, canRevoke: false })]);
    expect(screen.queryAllByRole("button")).toHaveLength(0);
  });

  it("approves without a code first and reports the change", async () => {
    const calls = mockApi({ "POST /grants/g1/approve": { status: 204 } });
    const onChanged = grants([grant({ status: "requested", canDecide: true })]);
    await userEvent.click(btn("Approve")!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => c.body)).toEqual([{}]);
  });

  it("shows the code gate on 403 reauth_required and retries the approval with the code", async () => {
    let asked = false;
    const calls = mockApi({
      "POST /grants/g1/approve": () => {
        if (!asked) {
          asked = true;
          return { status: 403, body: { title: "reauth_required" } };
        }
        return { status: 204 };
      },
    });
    const onChanged = grants([grant({ status: "requested", canDecide: true })]);
    expect(screen.queryByLabelText(en.remote.codeLabel)).toBeNull();
    await userEvent.click(btn("Approve")!);
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "654321");
    expect(onChanged).not.toHaveBeenCalled();
    await userEvent.click(btn("Approve")!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => c.body)).toEqual([{}, { code: "654321" }]);
  });

  it("denies and revokes through their own endpoints", async () => {
    const calls = mockApi({ "POST /grants/g1/deny": { status: 204 }, "POST /grants/g2/revoke": { status: 204 } });
    const onChanged = grants([grant({ id: "g1", status: "requested", canDecide: true }), grant({ id: "g2", canRevoke: true })]);
    await userEvent.click(btn("Deny")!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    await userEvent.click(btn("Revoke")!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(2));
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(["POST /grants/g1/deny", "POST /grants/g2/revoke"]);
  });

  it("shows another failure and does not report a change", async () => {
    mockApi({ "POST /grants/g1/revoke": { status: 409, body: { title: "not_pending" } } });
    const onChanged = grants([grant({ canRevoke: true })]);
    await userEvent.click(btn("Revoke")!);
    expect(await screen.findByText(en.errors.not_pending)).toBeInTheDocument();
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("offers the new-grant form only to someone who may create one", () => {
    grants([], true);
    expect(screen.getByText(en.remote.grantNew)).toBeInTheDocument();
  });

  it("does not offer the form to anyone else", () => {
    grants([], false);
    expect(screen.queryByText(en.remote.grantNew)).toBeNull();
  });
});

describe("the grant form", () => {
  const form = () => {
    const onChanged = grants([], true);
    return { onChanged, template: screen.getByLabelText(new RegExp(`^${en.remote.grantTemplate}`)), cwd: screen.getByLabelText(en.remote.cwd) };
  };

  it("sends one template element per line, drops blank lines and carriage returns, and trims the folder", async () => {
    const calls = mockApi({ "POST /agents/a1/grants": { status: 201, body: grant() } });
    const { onChanged, template, cwd } = form();
    await userEvent.type(template, "/usr/bin/ls{enter}{enter}-la{enter}  spaced arg {enter}");
    await userEvent.type(cwd, "  /srv/app  ");
    await userEvent.click(btn(en.remote.grantCreate)!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls[0]!.body).toEqual({ template: ["/usr/bin/ls", "-la", "  spaced arg "], cwd: "/srv/app", maxTimeoutSeconds: 60, days: config.grantDaysDefault });
  });

  it("sends the reason trimmed when given, the chosen limits, and clears template and reason afterwards", async () => {
    const calls = mockApi({ "POST /agents/a1/grants": { status: 201, body: grant() } });
    const { onChanged, template, cwd } = form();
    await userEvent.type(template, "/bin/true");
    await userEvent.type(cwd, "/");
    await userEvent.clear(screen.getByLabelText(en.remote.maxTimeout));
    await userEvent.type(screen.getByLabelText(en.remote.maxTimeout), "30");
    await userEvent.clear(screen.getByLabelText(en.remote.days));
    await userEvent.type(screen.getByLabelText(en.remote.days), "14");
    await userEvent.type(screen.getByLabelText(en.remote.reasonOptional), " nightly ");
    await userEvent.click(btn(en.remote.grantCreate)!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls[0]!.body).toEqual({ template: ["/bin/true"], cwd: "/", maxTimeoutSeconds: 30, days: 14, reason: "nightly" });
    expect(template).toHaveValue("");
    expect(screen.getByLabelText(en.remote.reasonOptional)).toHaveValue("");
  });

  it("refuses an empty template, a template of only blank lines, or no folder, without calling the API", async () => {
    const calls = mockApi({});
    const { template, cwd } = form();
    await userEvent.click(btn(en.remote.grantCreate)!);
    expect(screen.getByText(en.remote.grantIncomplete)).toBeInTheDocument();
    await userEvent.type(template, "{enter}{enter}");
    await userEvent.type(cwd, "/x");
    await userEvent.click(btn(en.remote.grantCreate)!);
    expect(screen.getByText(en.remote.grantIncomplete)).toBeInTheDocument();
    await userEvent.type(template, "/bin/true");
    await userEvent.clear(cwd);
    await userEvent.type(cwd, "   ");
    await userEvent.click(btn(en.remote.grantCreate)!);
    expect(screen.getByText(en.remote.grantIncomplete)).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it.each([
    ["time limit of 0", en.remote.maxTimeout, "0"], ["time limit above the maximum", en.remote.maxTimeout, String(config.grantTimeoutMax + 1)],
    ["0 days", en.remote.days, "0"], ["days above the maximum", en.remote.days, String(config.grantDaysMax + 1)],
  ])("refuses a %s as out of range", async (_name, label, value) => {
    const calls = mockApi({});
    const { template, cwd } = form();
    await userEvent.type(template, "/bin/true");
    await userEvent.type(cwd, "/x");
    await userEvent.clear(screen.getByLabelText(label));
    await userEvent.type(screen.getByLabelText(label), value);
    await userEvent.click(btn(en.remote.grantCreate)!);
    expect(screen.getByText(en.errors.out_of_range)).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it("accepts the limits exactly at the maximum", async () => {
    const calls = mockApi({ "POST /agents/a1/grants": { status: 201, body: grant() } });
    const { template, cwd } = form();
    await userEvent.type(template, "/bin/true");
    await userEvent.type(cwd, "/x");
    await userEvent.clear(screen.getByLabelText(en.remote.maxTimeout));
    await userEvent.type(screen.getByLabelText(en.remote.maxTimeout), String(config.grantTimeoutMax));
    await userEvent.clear(screen.getByLabelText(en.remote.days));
    await userEvent.type(screen.getByLabelText(en.remote.days), String(config.grantDaysMax));
    await userEvent.click(btn(en.remote.grantCreate)!);
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]!.body).toMatchObject({ maxTimeoutSeconds: config.grantTimeoutMax, days: config.grantDaysMax });
  });

  it("shows the code gate on 403 reauth_required and retries with the code, keeping the template", async () => {
    let asked = false;
    const calls = mockApi({
      "POST /agents/a1/grants": () => {
        if (!asked) {
          asked = true;
          return { status: 403, body: { title: "reauth_required" } };
        }
        return { status: 201, body: grant() };
      },
    });
    const { onChanged, template, cwd } = form();
    await userEvent.type(template, "/bin/true");
    await userEvent.type(cwd, "/x");
    await userEvent.click(btn(en.remote.grantCreate)!);
    const field = await screen.findByLabelText(en.remote.codeLabel);
    expect(template).toHaveValue("/bin/true");
    expect(onChanged).not.toHaveBeenCalled();
    await userEvent.type(field, "111222");
    await userEvent.click(btn(en.remote.grantCreate)!);
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    expect(calls[0]!.body).not.toHaveProperty("code");
    expect(calls[1]!.body).toMatchObject({ template: ["/bin/true"], code: "111222" });
  });

  it("shows the API's reason when the template is refused and keeps what was typed", async () => {
    mockApi({ "POST /agents/a1/grants": { status: 400, body: { errors: { template: ["bad_template"] } } } });
    const { onChanged, template, cwd } = form();
    await userEvent.type(template, "ls");
    await userEvent.type(cwd, "/x");
    await userEvent.click(btn(en.remote.grantCreate)!);
    expect(await screen.findByText(en.errors.bad_template)).toBeInTheDocument();
    expect(template).toHaveValue("ls");
    expect(onChanged).not.toHaveBeenCalled();
  });
});

describe("the job list", () => {
  const jobs = (list: ReturnType<typeof job>[], onChanged = vi.fn()) => {
    renderPlain(<JobList jobs={list} onChanged={onChanged} />);
    return onChanged;
  };

  it("says there are no jobs, and shows a job's name, frozen command, folder and limit", () => {
    const { unmount } = renderPlain(<JobList jobs={[]} onChanged={vi.fn()} />);
    expect(screen.getByText(en.remote.noJobs)).toBeInTheDocument();
    unmount();
    jobs([job({ name: "night‮ly", proposedByHostname: "laptop-9", reason: "keep backups" })]);
    expect(screen.getByRole("strong")).toHaveTextContent("night\\u202Ely");
    expect(within(screen.getByRole("list", { name: en.remote.argvList })).getAllByRole("listitem")).toHaveLength(2);
    expect(screen.getByText("300 s")).toBeInTheDocument();
    expect(screen.getByText(/proposed by Diğer Kişi · laptop-9/)).toBeInTheDocument();
    expect(screen.getByText(en.remote.jobStatus.proposed)).toBeInTheDocument();
    expect(screen.getByText(en.remote.reasonUntrusted)).toBeInTheDocument();
  });

  it("gives the owner approve and deny for a proposed job, a retire button for an active one, and others nothing", () => {
    jobs([job({ id: "j1", canDecide: true }), job({ id: "j2", status: "active", canRetire: true }), job({ id: "j3" })]);
    const rows = screen.getAllByRole("listitem").filter((li) => li.classList.contains("grant-row"));
    expect(rows).toHaveLength(3);
    expect(within(rows[0]!).getAllByRole("button").map((b) => b.textContent)).toEqual(["Deny", "Approve"]);
    expect(within(rows[1]!).getAllByRole("button").map((b) => b.textContent)).toEqual(["Retire"]);
    expect(within(rows[2]!).queryAllByRole("button")).toHaveLength(0);
  });

  it("approves with the code after 403 reauth_required, and retires and denies through their endpoints", async () => {
    let asked = false;
    const calls = mockApi({
      "POST /jobs/j1/approve": () => {
        if (!asked) {
          asked = true;
          return { status: 403, body: { title: "reauth_required" } };
        }
        return { status: 204 };
      },
      "POST /jobs/j2/retire": { status: 204 },
      "POST /jobs/j3/deny": { status: 204 },
    });
    const onChanged = jobs([job({ id: "j1", canDecide: true }), job({ id: "j2", status: "active", canRetire: true }), job({ id: "j3", canDecide: true })]);
    const rows = () => screen.getAllByRole("listitem").filter((li) => li.classList.contains("grant-row"));
    await userEvent.click(within(rows()[0]!).getByRole("button", { name: "Approve" }));
    await userEvent.type(await screen.findByLabelText(en.remote.codeLabel), "999000");
    await userEvent.click(within(rows()[0]!).getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
    await userEvent.click(within(rows()[1]!).getByRole("button", { name: "Retire" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(2));
    await userEvent.click(within(rows()[2]!).getByRole("button", { name: "Deny" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(3));
    expect(calls.map((c) => `${c.path} ${JSON.stringify(c.body ?? null)}`)).toEqual([
      "/jobs/j1/approve {}", '/jobs/j1/approve {"code":"999000"}', "/jobs/j2/retire null", "/jobs/j3/deny null",
    ]);
  });
});
