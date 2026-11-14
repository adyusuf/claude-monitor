import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { CommandRow, PermissionRow } from "../api/types";
import { type Call, ME, mockApi, renderAt } from "../test/helpers";
import { CommandsCard, Composer, PermissionsCard } from "./SessionControls";

afterEach(() => vi.restoreAllMocks());

const recent = () => new Date().toISOString();
const sender = (over: Partial<Parameters<typeof Composer>[0]> = {}) => ({
  sessionId: "s1", canCommand: true, ended: false, lastEventAt: recent(), reloadRows: vi.fn(), onChange: vi.fn(), ...over,
});
const show = (ui: ReactNode) => renderAt("/x", [{ path: "/x", element: ui }]);
const posts = (calls: Call[]) => calls.filter((c) => c.method === "POST");

describe("the box that sends a message to a session", () => {
  it("refuses an empty message with a text and sends nothing", async () => {
    const calls = mockApi({ "GET /me": { body: ME } });
    show(<Composer {...sender()} />);
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(screen.getByRole("alert")).toHaveTextContent("Write the message to send.");
    expect(posts(calls)).toEqual([]);
  });

  it("refuses a message of only spaces the same way", async () => {
    const calls = mockApi({ "GET /me": { body: ME } });
    show(<Composer {...sender()} />);
    await userEvent.type(screen.getByRole("textbox"), "   ");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(screen.getByRole("alert")).toHaveTextContent("Write the message to send.");
    expect(posts(calls)).toEqual([]);
  });

  it("shows the API's error and keeps what was typed when the send fails", async () => {
    const own = sender();
    mockApi({ "GET /me": { body: ME }, "POST /sessions/s1/commands": { status: 400, body: { title: "too_long" } } });
    show(<Composer {...own} />);
    await userEvent.type(screen.getByRole("textbox"), "Run the tests");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That is too long.");
    expect(screen.getByRole("textbox")).toHaveValue("Run the tests");
    expect(own.onChange).not.toHaveBeenCalled();
  });

  it("clears the box, says it was sent and reloads the commands after a good send", async () => {
    const own = sender();
    const calls = mockApi({ "GET /me": { body: ME }, "POST /sessions/s1/commands": { status: 201, body: { id: "c2" } } });
    show(<Composer {...own} />);
    await userEvent.type(screen.getByRole("textbox"), "Run the tests");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Sent.");
    expect(screen.getByRole("textbox")).toHaveValue("");
    expect(posts(calls).map((c) => c.body)).toEqual([{ kind: "prompt", body: "Run the tests" }]);
    expect(own.reloadRows).toHaveBeenCalledTimes(1);
    expect(own.onChange).toHaveBeenCalledTimes(1);
  });

  it("sends no stop when the person declines the confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    const calls = mockApi({ "GET /me": { body: ME } });
    show(<Composer {...sender()} />);
    await userEvent.click(screen.getByRole("button", { name: "Stop the session" }));
    expect(posts(calls)).toEqual([]);
  });

  it("shows the API's error when the stop fails", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockApi({ "GET /me": { body: ME }, "POST /sessions/s1/commands": { status: 409, body: { title: "session_ended" } } });
    show(<Composer {...sender()} />);
    await userEvent.click(screen.getByRole("button", { name: "Stop the session" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The session has ended.");
  });

  it("takes no message for an ended session and says so, and tells a non-owner that only the owner sends", async () => {
    mockApi({ "GET /me": { body: ME } });
    const ended = show(<Composer {...sender({ ended: true })} />);
    expect(screen.getByText("The session has ended.")).toBeInTheDocument();
    expect(screen.queryByRole("textbox")).toBeNull();
    ended.unmount();
    show(<Composer {...sender({ canCommand: false })} />);
    expect(screen.getByText("Only the person whose machine runs this session can send it commands.")).toBeInTheDocument();
    expect(screen.queryByRole("textbox")).toBeNull();
  });
});

describe("cancelling a command that is still waiting", () => {
  const queued: CommandRow = {
    id: "c1", kind: "prompt", body: "earlier", status: "queued", createdBy: "u1", createdAt: recent(), expiresAt: new Date(Date.now() + 600_000).toISOString(),
    deliveredAt: null, appliedAt: null, result: null,
  };

  it("shows the API's error when the cancel fails, and does not reload the list as if it had worked", async () => {
    const own = sender();
    mockApi({ "GET /me": { body: ME }, "POST /commands/c1/cancel": { status: 409, body: {} } });
    show(<CommandsCard {...own} rows={[queued]} withComposer={false} />);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That has already changed. Refresh and try again.");
    expect(own.reloadRows).not.toHaveBeenCalled();
  });

  it("posts the cancel for that command and reloads the list when it works", async () => {
    const own = sender();
    const calls = mockApi({ "GET /me": { body: ME }, "POST /commands/c1/cancel": { status: 204 } });
    show(<CommandsCard {...own} rows={[queued]} withComposer={false} />);
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(own.reloadRows).toHaveBeenCalledTimes(1));
    expect(posts(calls).map((c) => c.path)).toEqual(["/commands/c1/cancel"]);
  });

  it("offers no cancel to someone who cannot command the session", () => {
    mockApi({ "GET /me": { body: ME } });
    show(<CommandsCard {...sender({ canCommand: false })} rows={[queued]} withComposer={false} />);
    expect(screen.queryByRole("button", { name: "Cancel" })).toBeNull();
  });
});

describe("answering a permission request", () => {
  const request: PermissionRow = {
    id: "p1", toolName: "Bash", toolInput: { command: "git push" }, status: "open", createdAt: recent(), expiresAt: new Date(Date.now() + 600_000).toISOString(),
    decision: null, reason: null, answeredAt: null,
  };

  it("shows the API's error and keeps the request listed when the answer fails", async () => {
    const own = sender();
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1/permission-requests": { body: [request] },
      "POST /permission-requests/p1/answer": { status: 409, body: {} },
    });
    show(<PermissionsCard sessionId="s1" canAnswer version={0} onChange={own.onChange} />);
    await userEvent.click(await screen.findByRole("button", { name: "Allow" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That has already changed. Refresh and try again.");
    expect(screen.getByRole("button", { name: "Allow" })).toBeInTheDocument();
    expect(own.onChange).not.toHaveBeenCalled();
  });

  it("takes the request off the list and tells the page once the answer is accepted", async () => {
    const own = sender();
    mockApi({
      "GET /me": { body: ME },
      "GET /sessions/s1/permission-requests": { body: [request] },
      "POST /permission-requests/p1/answer": { status: 204 },
    });
    show(<PermissionsCard sessionId="s1" canAnswer version={0} onChange={own.onChange} />);
    await userEvent.click(await screen.findByRole("button", { name: "Deny" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Deny" })).toBeNull());
    expect(own.onChange).toHaveBeenCalledTimes(1);
  });

  it("shows no answer buttons to someone who cannot answer, only who can", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /sessions/s1/permission-requests": { body: [request] } });
    show(<PermissionsCard sessionId="s1" canAnswer={false} version={0} onChange={() => undefined} />);
    expect(await screen.findByText("Only the person whose machine runs this session can send it commands.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Allow" })).toBeNull();
  });

  it("shows the API's error when the list of requests cannot be read", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /sessions/s1/permission-requests": { status: 403, body: { title: "forbidden" } } });
    show(<PermissionsCard sessionId="s1" canAnswer version={0} onChange={() => undefined} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("You are not allowed to do that.");
  });
});
