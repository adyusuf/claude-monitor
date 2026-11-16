import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { approvalKeyScope } from "../lib/approvalKey";
import { ME, mockApi, renderAt } from "../test/helpers";
import { RequireAuth, useSession } from "./session";

function SignOut() {
  const { signOut } = useSession();
  return <button type="button" onClick={() => void signOut()}>out</button>;
}

afterEach(() => vi.restoreAllMocks());

describe("the approval key follows the person signed in (F-190)", () => {
  it("points the key store at the signed-in person before the page opens, and at nobody after signing out", async () => {
    const seen = vi.spyOn(approvalKeyScope, "useFor");
    mockApi({ "GET /me": { body: ME }, "POST /auth/logout": { status: 204 } });
    renderAt("/page", [{ path: "/page", element: <RequireAuth><SignOut /></RequireAuth> }, { path: "/login", element: <div>login</div> }]);

    await userEvent.click(await screen.findByRole("button", { name: "out" }));

    await waitFor(() => expect(seen.mock.calls).toEqual([[ME.id], [null]]));
  });
});
