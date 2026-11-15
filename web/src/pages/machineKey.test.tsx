import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Link } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { en } from "../i18n/en";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { machine } from "../test/remote-fixtures";
import { MachinePage } from "./MachinePage";
import { agent } from "./machinesFixture";

beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
afterEach(() => vi.unstubAllGlobals());

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

describe("moving from one machine to another", () => {
  it("starts the other machine with nothing left of what was typed for the first", async () => {
    api({
      "GET /workspaces/w1/machines": { body: [machine(), machine({ agentId: "a2", hostname: "laptop-2" })] },
      "GET /workspaces/w1/agents": { body: [agent({ userId: "u1" }), agent({ id: "a2", userId: "u1", hostname: "laptop-2" })] },
      "GET /agents/a2/grants": { body: [] },
      "GET /agents/a2/jobs": { body: [] },
      "GET /agents/a2/metrics": { body: [] },
    });
    renderAt("/w/w1/machines/a1", [{
      path: "/w/:ws/machines/:agentId",
      element: <><Link to="/w/w1/machines/a2">next machine</Link><MachinePage /></>,
    }]);
    const note = await screen.findByLabelText(en.machines.note.label);
    await userEvent.type(note, "draft for the first machine");
    expect(note).toHaveValue("draft for the first machine");

    await userEvent.click(screen.getByRole("link", { name: "next machine" }));

    expect(await screen.findByRole("heading", { level: 1, name: /laptop-2/ })).toBeInTheDocument();
    expect(screen.getByLabelText(en.machines.note.label)).toHaveValue("");
  });
});
