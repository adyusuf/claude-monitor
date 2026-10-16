import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ScheduleView, WebJobView } from "../api/types";
import { en } from "../i18n/en";
import { mockApi } from "../test/helpers";
import { renderPlain } from "../test/remote-fixtures";
import { clock, minutesOf, ScheduleList } from "./ScheduleList";

const schedule = (over: Partial<ScheduleView> = {}): ScheduleView => ({
  id: "s1", jobId: "j1", jobName: "Run tests", targetAgentId: "a1", kind: "interval", intervalMinutes: 60, dailyAtMinutes: null, status: "active", pauseReason: null,
  nextRunAt: new Date(Date.now() + 30 * 60_000).toISOString(), lastRunAt: null, lastRunStatus: null, failuresInARow: 0, ownerUserId: "u1", isOwner: true, createdAt: new Date().toISOString(), ...over,
});

const job = (over: Partial<WebJobView> = {}): WebJobView => ({
  id: "j1", targetAgentId: "a1", ownerUserId: "u1", name: "Run tests", argv: ["/opt/ci/run-tests"], cwd: "/opt/ci", timeoutSeconds: 300, reason: null, status: "active",
  proposedByUserId: "u2", proposedByName: "P", proposedByAgentId: null, proposedByHostname: null, createdAt: new Date().toISOString(), decidedAt: null, retiredAt: null, canDecide: false, canRetire: true, ...over,
});

const show = (schedules: ScheduleView[], jobs: WebJobView[] = [job()], canCreate = true) =>
  renderPlain(<ScheduleList schedules={schedules} jobs={jobs} canCreate={canCreate} onChanged={vi.fn()} />);

describe("the times of a schedule", () => {
  it("turns minutes after midnight into a clock time and back, and refuses anything else", () => {
    expect(clock(0)).toBe("00:00");
    expect(clock(185)).toBe("03:05");
    expect(clock(1439)).toBe("23:59");
    expect(minutesOf("03:05")).toBe(185);
    expect(minutesOf(" 3:05 ")).toBe(185);
    expect(minutesOf("23:59")).toBe(1439);
    for (const bad of ["24:00", "12:60", "noon", "", "1:5", "12:30:00"]) expect(minutesOf(bad)).toBeNull();
  });
});

describe("the schedules of a machine", () => {
  it("says there are none, and lists a schedule with its job, repeat, next run and status", () => {
    show([]);
    expect(screen.getByText(en.schedules.none)).toBeInTheDocument();
  });

  it("shows an interval and a daily schedule in words and what stopped a paused one", () => {
    show([schedule(), schedule({ id: "s2", kind: "daily", intervalMinutes: null, dailyAtMinutes: 185, jobName: "Nightly" }),
      schedule({ id: "s3", status: "paused", pauseReason: "failures", failuresInARow: 3, nextRunAt: null, jobName: "Broken" })]);
    expect(screen.getByText("every 60 minutes")).toBeInTheDocument();
    expect(screen.getByText("every day at 03:05 UTC")).toBeInTheDocument();
    expect(screen.getByText(en.schedules.reason.failures)).toBeInTheDocument();
    expect(screen.getByText(en.schedules.status.paused)).toBeInTheDocument();
    expect(screen.getByText("3")).toBeInTheDocument();
  });

  it("pauses a running schedule and ends one, and starts a paused one again with the code gate", async () => {
    const calls = mockApi({ "POST /schedules/s1/pause": { status: 204 }, "POST /schedules/s2/end": { status: 204 }, "POST /schedules/s3/resume": { status: 204 } });
    show([schedule(), schedule({ id: "s2", jobName: "Second" }), schedule({ id: "s3", status: "paused", pauseReason: "owner", nextRunAt: null, jobName: "Third" })]);
    await userEvent.click(screen.getAllByRole("button", { name: en.schedules.pause })[0]!);
    await userEvent.click(screen.getAllByRole("button", { name: en.schedules.end })[1]!);
    await userEvent.click(screen.getByRole("button", { name: en.schedules.resume }));
    await waitFor(() => expect(calls.map((c) => c.path)).toEqual(expect.arrayContaining(["/schedules/s1/pause", "/schedules/s2/end", "/schedules/s3/resume"])));
  });

  it("offers no buttons for an ended schedule or one that is not the person's", () => {
    show([schedule({ status: "ended", nextRunAt: null }), schedule({ id: "s2", isOwner: false, jobName: "Theirs" })], [job()], false);
    expect(screen.queryByRole("button", { name: en.schedules.pause })).toBeNull();
    expect(screen.queryByRole("button", { name: en.schedules.end })).toBeNull();
    expect(screen.queryByRole("button", { name: en.schedules.create })).toBeNull(); // not the machine's owner: no form
  });

  it("shows why a schedule stopped in words for each reason the API can give", () => {
    const reasons = Object.keys(en.schedules.reason);
    show(reasons.map((r, i) => schedule({ id: `s${i}`, status: "paused", pauseReason: r, nextRunAt: null, jobName: `J${i}` })));
    for (const text of Object.values(en.schedules.reason)) expect(screen.getByText(text)).toBeInTheDocument();
  });
});

describe("starting a schedule", () => {
  it("says what is needed when there is no usable job (none active, or a placeholder in the command)", () => {
    show([], [job({ status: "proposed" }), job({ id: "j2", argv: ["/opt/ci/run", "{ref}"] })]);
    expect(screen.getByText(en.schedules.noJobs)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: en.schedules.create })).toBeNull();
  });

  it("sends an interval schedule for the chosen job", async () => {
    const calls = mockApi({ "POST /schedules": { body: schedule() } });
    show([], [job(), job({ id: "j2", name: "Other" })]);
    await userEvent.selectOptions(screen.getByLabelText(en.schedules.job), "j2");
    const minutes = screen.getByLabelText(en.schedules.minutes);
    await userEvent.clear(minutes);
    await userEvent.type(minutes, "30");
    await userEvent.click(screen.getByRole("button", { name: en.schedules.create }));
    await waitFor(() => expect(calls.some((c) => c.path === "/schedules")).toBe(true));
    expect(calls.find((c) => c.path === "/schedules")!.body).toMatchObject({ jobId: "j2", kind: "interval", intervalMinutes: 30 });
  });

  it("sends a daily schedule with the time in minutes after midnight", async () => {
    const calls = mockApi({ "POST /schedules": { body: schedule() } });
    show([]);
    await userEvent.selectOptions(screen.getByLabelText(en.schedules.kind), "daily");
    const time = screen.getByLabelText(en.schedules.timeUtc);
    await userEvent.clear(time);
    await userEvent.type(time, "03:05");
    await userEvent.click(screen.getByRole("button", { name: en.schedules.create }));
    await waitFor(() => expect(calls.some((c) => c.path === "/schedules")).toBe(true));
    expect(calls.find((c) => c.path === "/schedules")!.body).toMatchObject({ jobId: "j1", kind: "daily", dailyAtMinutes: 185 });
  });

  it("refuses an interval outside 15 minutes to 7 days and a time that is not a time, before anything is sent", async () => {
    const calls = mockApi({ "POST /schedules": { body: schedule() } });
    show([]);
    const minutes = screen.getByLabelText(en.schedules.minutes);
    for (const bad of ["14", "10081", "1.5"]) {
      await userEvent.clear(minutes);
      await userEvent.type(minutes, bad);
      await userEvent.click(screen.getByRole("button", { name: en.schedules.create }));
      expect(await screen.findByText(en.errors.invalid_schedule)).toBeInTheDocument();
    }

    await userEvent.selectOptions(screen.getByLabelText(en.schedules.kind), "daily");
    const time = screen.getByLabelText(en.schedules.timeUtc);
    await userEvent.clear(time);
    await userEvent.type(time, "25:00");
    await userEvent.click(screen.getByRole("button", { name: en.schedules.create }));
    expect(await screen.findByText(en.errors.invalid_schedule)).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/schedules")).toBe(false);
  });

  it("shows what the API says when schedules are off", async () => {
    mockApi({ "POST /schedules": { status: 409, body: { title: "schedules_disabled" } } });
    show([]);
    await userEvent.click(screen.getByRole("button", { name: en.schedules.create }));
    expect(await screen.findByText(en.errors.schedules_disabled)).toBeInTheDocument();
  });
});
