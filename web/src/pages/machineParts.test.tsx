import { screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { alert, renderPlain, sample } from "../test/remote-fixtures";
import { AlertList } from "./AlertList";
import { ExecBadge, maxDiskPct, memoryPct, OnlineDot, ServiceBadge } from "./MachineBits";
import { MachineMetrics } from "./MachineMetrics";

const at = (h: number, m: number, s: number) => new Date(2026, 9, 5, h, m, s).toISOString();

describe("machine metrics", () => {
  it("says so when there are no measurements", () => {
    renderPlain(<MachineMetrics samples={[]} />);
    expect(screen.getByText(en.remote.noMetrics)).toBeInTheDocument();
    expect(screen.queryByRole("img")).toBeNull();
  });

  it("draws CPU and memory lines through every sample, 0 at the bottom and 100 at the top", () => {
    const { container } = renderPlain(<MachineMetrics samples={[
      sample({ sampledAt: at(9, 0, 0), cpuPct: 0, memUsedBytes: 0, memTotalBytes: 100 }),
      sample({ sampledAt: at(9, 1, 0), cpuPct: 100, memUsedBytes: 50, memTotalBytes: 100 }),
      sample({ sampledAt: at(9, 2, 5), cpuPct: 50, memUsedBytes: 100, memTotalBytes: 100 }),
    ]} />);
    const lines = [...container.querySelectorAll("polyline")].map((p) => p.getAttribute("points"));
    expect(lines).toEqual(["0.00,32.00 50.00,0.00 100.00,16.00", "0.00,32.00 50.00,16.00 100.00,0.00"]);
    expect(screen.getByRole("img", { name: "CPU 50%" })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Memory 100%" })).toBeInTheDocument();
    expect(screen.getAllByText("09:00:00")).toHaveLength(2);
    expect(screen.getAllByText("09:02:05")).toHaveLength(2);
  });

  it("clamps a value outside 0-100 into the box", () => {
    const { container } = renderPlain(<MachineMetrics samples={[sample({ cpuPct: -20 }), sample({ cpuPct: 250 })]} />);
    expect(container.querySelector("polyline")!.getAttribute("points")).toBe("0.00,32.00 100.00,0.00");
  });

  it("draws a single sample as a dot, not a line", () => {
    const { container } = renderPlain(<MachineMetrics samples={[sample({ cpuPct: 75 })]} />);
    expect(container.querySelector("polyline")).toBeNull();
    const dots = container.querySelectorAll("circle");
    expect(dots).toHaveLength(2);
    expect(dots[0]!.getAttribute("cy")).toBe(String(32 - 0.75 * 32));
  });

  it("shows each disk of the latest sample with sizes, share and a bar capped at 100%", () => {
    renderPlain(<MachineMetrics samples={[
      sample({ disks: [{ mount: "/old", usedBytes: 1, totalBytes: 2 }] }),
      sample({ disks: [{ mount: "/", usedBytes: 512 * 1024 * 1024, totalBytes: 1024 * 1024 * 1024 }, { mount: "/over", usedBytes: 300, totalBytes: 200 }, { mount: "/empty", usedBytes: 0, totalBytes: 0 }] }),
    ]} />);
    expect(screen.queryByText("/old")).toBeNull();
    const root = screen.getByText("/").closest("li")!;
    expect(root).toHaveTextContent("512.0 MB / 1024.0 MB · 50%");
    expect(within(root).getByRole("img", { name: "/ 50%" }).firstElementChild).toHaveStyle({ width: "50%" });
    const over = screen.getByText("/over").closest("li")!;
    expect(within(over).getByRole("img", { name: "/over 150%" }).firstElementChild).toHaveStyle({ width: "100%" });
    expect(within(screen.getByText("/empty").closest("li")!).getByRole("img", { name: "/empty 0%" })).toBeInTheDocument();
  });

  it("says no disk was measured when the latest sample has none", () => {
    renderPlain(<MachineMetrics samples={[sample({ disks: [] })]} />);
    expect(screen.getByText(en.remote.noDisks)).toBeInTheDocument();
  });
});

describe("machine bits", () => {
  it("names the exec level, treating anything unknown as off", () => {
    const cases: [string, string, string][] = [["off", "Runs: off", "exec-off"], ["argv", "Runs: commands", "exec-argv"], ["shell", "Runs: shell", "exec-shell"], ["future", "Runs: off", "exec-off"]];
    for (const [level, label, cls] of cases) {
      const { unmount } = renderPlain(<ExecBadge level={level} />);
      expect(screen.getByText(label)).toHaveClass(cls);
      unmount();
    }
  });

  it("shows the service badge only in service mode", () => {
    const { unmount } = renderPlain(<ServiceBadge serviceMode={true} />);
    expect(screen.getByText(en.remote.service)).toBeInTheDocument();
    unmount();
    renderPlain(<ServiceBadge serviceMode={false} />);
    expect(screen.queryByText(en.remote.service)).toBeNull();
  });

  it("labels the online dot for screen readers", () => {
    const { unmount } = renderPlain(<OnlineDot online={true} />);
    expect(screen.getByRole("img", { name: "Online" })).toHaveClass("online-on");
    unmount();
    renderPlain(<OnlineDot online={false} />);
    expect(screen.getByRole("img", { name: "Offline" })).not.toHaveClass("online-on");
  });

  it("computes memory and the fullest disk, null when nothing was measured", () => {
    expect(memoryPct(null)).toBeNull();
    expect(memoryPct(sample({ memUsedBytes: 1, memTotalBytes: 4 }))).toBe(25);
    expect(maxDiskPct(null)).toBeNull();
    expect(maxDiskPct(sample({ disks: [] }))).toBeNull();
    expect(maxDiskPct(sample({ disks: [{ mount: "/a", usedBytes: 10, totalBytes: 100 }, { mount: "/b", usedBytes: 90, totalBytes: 100 }, { mount: "/c", usedBytes: 20, totalBytes: 100 }] }))).toBe(90);
  });
});

describe("alert list", () => {
  it("says there are no alerts", () => {
    renderPlain(<AlertList alerts={[]} />);
    expect(screen.getByText(en.remote.noAlerts)).toBeInTheDocument();
  });

  it("shows an open alert with its numbers, and a resolved one with the time it closed", () => {
    renderPlain(<AlertList alerts={[
      alert({ id: "1", kind: "disk", subject: "/data‮", lastValue: 95.4, peakValue: 99.5, thresholdPct: 90 }),
      alert({ id: "2", kind: "memory", state: "resolved", resolvedAt: new Date(2026, 9, 5, 10, 45).toISOString() }),
    ]} />);
    const [open, closed] = screen.getAllByRole("listitem");
    expect(open).toHaveClass("alert-open");
    expect(within(open!).getByText("Open")).toBeInTheDocument();
    expect(within(open!).getByText("Disk")).toBeInTheDocument();
    expect(open).toHaveTextContent("/data\\u202E");
    expect(open).toHaveTextContent("now 95%, peak 100%, threshold 90%");
    expect(open).toHaveTextContent("05/10/2026 09:30");
    expect(open).not.toHaveTextContent("→");
    expect(closed).not.toHaveClass("alert-open");
    expect(within(closed!).getByText("Resolved")).toBeInTheDocument();
    expect(closed).toHaveTextContent("05/10/2026 09:30 → 05/10/2026 10:45");
  });

  it("shows no numbers for an offline alert and no subject when there is none", () => {
    renderPlain(<AlertList alerts={[alert({ kind: "offline", subject: "" })]} />);
    const row = screen.getByRole("listitem");
    expect(row).toHaveTextContent("Offline");
    expect(row).not.toHaveTextContent("threshold");
    expect(row.querySelector("code")).toBeNull();
  });
});
