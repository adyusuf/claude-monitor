import { screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NEXT_STEP_TOOL } from "../lib/transcript";
import { FakeEventSource, ME, mockApi, renderAt } from "../test/helpers";
import { at, call, ev, user } from "../test/transcript-fixtures";
import { SessionPage } from "./SessionPage";

const session = { id: "s1", harnessKind: "claude_code", title: "Chat", model: null, gitBranch: null, status: "active", startedAt: at, lastEventAt: at, endedAt: null,
  projectId: null, projectName: null, agentId: "a", hostname: "h", ownerId: "u1", ownerName: "O", costUsd: null, openPermissions: 0 };
const route = [{ path: "/w/:ws/sessions/:id", element: <SessionPage /> }];
const thinking = (id: number, text: string) => ev(id, { type: "assistant", message: { role: "assistant", content: [{ type: "thinking", thinking: text }] } });

describe("formatted text in the conversation", () => {
  beforeEach(() => vi.stubGlobal("EventSource", FakeEventSource));
  afterEach(() => vi.unstubAllGlobals());

  it("formats what the user typed, what Claude thought and the next-step question, not only Claude's replies", async () => {
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1": { body: { session, canCommand: true, tasks: [], subagents: [], usage: [] } },
      "GET /sessions/s1/commands": { body: [] },
      "GET /sessions/s1/permission-requests": { body: [] },
      "GET /sessions/s1/events": { body: { items: [
        call(3, "t1", NEXT_STEP_TOOL, { recap: "Merged **both**.", ask: "Push?\n\n- to `prod`\n- to `test`", actions: [{ label: "Yes" }] }),
        thinking(2, "I will **plan** it\n\n1. read\n2. write"),
        user(1, "Please **check** `config.ts`\n\n- one\n- two"),
      ], next: null } },
    });
    renderAt("/w/w1/sessions/s1", route);

    const bubble = (await screen.findByText("check")).closest(".chat-bubble") as HTMLElement;
    expect(bubble.querySelector("strong")?.textContent).toBe("check");
    expect(bubble.querySelector("code")?.textContent).toBe("config.ts");
    expect(bubble.querySelectorAll("li")).toHaveLength(2);
    const thought = document.querySelector(".chat-thinking") as HTMLElement;
    expect(thought.querySelector("strong")?.textContent).toBe("plan");
    expect(thought.querySelectorAll("ol li")).toHaveLength(2);
    const card = document.querySelector(".chat-nextstep") as HTMLElement;
    expect(card.querySelector("strong")?.textContent).toBe("both");
    expect([...card.querySelectorAll("li code")].map((c) => c.textContent)).toEqual(["prod", "test"]);
  });
});
