import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { RemoteSettings } from "../api/types";
import { en } from "../i18n/en";
import { ME, mockApi, renderAt } from "../test/helpers";
import { RemoteSettingsCard } from "./RemoteSettingsCard";

const MIB = 1024 * 1024;
const settings = (over: Partial<RemoteSettings> = {}): RemoteSettings => ({
  remoteRunsEnabled: true, alertCpuPct: 90, alertMemoryPct: 85, alertDiskPct: 80, alertSustainSeconds: 300, scriptOutputMaxBytes: 8 * MIB, ...over,
});
const card = () => renderAt("/w/w1", [{ path: "/w/:ws", element: <RemoteSettingsCard ws="w1" canEdit /> }]);

describe("the output limit of script runs in the remote settings", () => {
  it("shows the limit in megabytes and saves the new one in bytes", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card();
    const cap = await screen.findByLabelText(en.remote.outputCap);
    expect(cap).toHaveValue(8);
    await userEvent.clear(cap);
    await userEvent.type(cap, "16");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText(en.workspace.saved)).toBeInTheDocument();
    expect(calls.find((c) => c.method === "PUT")?.body).toMatchObject({ scriptOutputMaxBytes: 16 * MIB });
  });

  it("refuses a limit outside 1 to 64 MB without calling the API", async () => {
    const calls = mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings() }, "PUT /workspaces/w1/remote-settings": (c) => ({ body: c.body }) });
    card();
    const cap = await screen.findByLabelText(en.remote.outputCap);
    for (const bad of ["0", "65"]) {
      await userEvent.clear(cap);
      await userEvent.type(cap, bad);
      await userEvent.click(screen.getByRole("button", { name: "Save" }));
      expect(await screen.findByText(en.errors.out_of_range)).toBeInTheDocument();
    }

    expect(calls.filter((c) => c.method === "PUT")).toHaveLength(0);
  });

  it("has no such field when the API does not know the limit (an older server)", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/remote-settings": { body: settings({ scriptOutputMaxBytes: undefined }) } });
    card();
    await screen.findByLabelText(en.remote.alertCpu);
    expect(screen.queryByLabelText(en.remote.outputCap)).toBeNull();
  });
});
