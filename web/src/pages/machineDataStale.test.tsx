import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config } from "../config";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, openTab, renderAt } from "../test/helpers";
import { alert, machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const route = [{ path: "/w/:ws/machines/:agentId", element: <MachinePage /> }];
const open = () => renderAt("/w/w1/machines/a1", route);

function api(over: Parameters<typeof mockApi>[0] = {}) {
  return mockApi({
    "GET /me": { body: ME },
    "GET /workspaces/w1/machines": { body: [machine()] },
    "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" })] },
    "GET /workspaces/w1/alerts": { body: [] },
    "GET /workspaces/w1/runs": { body: [] },
    "GET /agents/a1/grants": { body: [] },
    "GET /agents/a1/jobs": { body: [] },
    "GET /agents/a1/metrics": { body: [] },
    ...over,
  });
}

/** A live message and the debounce after it: the page reads everything again. */
async function liveReread(event = "run") {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  act(() => FakeEventSource.last!.emit(event, {}));
  await act(() => vi.advanceTimersByTimeAsync(config.liveDebounceMs));
  vi.useRealTimers();
}

describe("a re-read of the machine page that is overtaken by a newer one", () => {
  it("shows the alerts of the filter chosen last, not those of an older read that answers late", async () => {
    api({
      "GET /workspaces/w1/alerts": (c) => c.path.includes("resolved=true")
        ? { body: [alert({ id: "r1", subject: "resolved-alert", state: "resolved" })] }
        : { body: [alert({ id: "o1", subject: "open-alert" })] },
    });
    open();
    await openTab(en.views.machine.monitoring);
    expect(await screen.findByText("open-alert")).toBeInTheDocument();

    // The next read of the open alerts is answered by the server at once but reaches the page only when released.
    const inner = vi.mocked(fetch).getMockImplementation()!;
    let release: () => void = () => {};
    let armed = true;
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const response = await inner(input, init);
      if (armed && String(input).includes("/alerts") && String(input).includes("resolved=false")) {
        armed = false;
        await new Promise<void>((resolve) => {
          release = resolve;
        });
      }
      return response;
    });

    await liveReread("alert");
    await userEvent.click(screen.getByRole("checkbox", { name: en.remote.showResolved }));
    expect(await screen.findByText("resolved-alert")).toBeInTheDocument();
    expect(armed).toBe(false);

    await act(async () => release());
    await act(async () => {});

    expect(screen.getByText("resolved-alert")).toBeInTheDocument();
    expect(screen.queryByText("open-alert")).toBeNull();
  });
});

