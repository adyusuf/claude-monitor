import { screen } from "@testing-library/react";
import { useEffect, useState } from "react";
import { useLocation } from "react-router";
import { describe, expect, it } from "vitest";
import { ApiError, request } from "../api/client";
import { ME, mockApi, renderAt } from "../test/helpers";
import { RequireAuth } from "./session";

/** A page that makes one call when it opens and shows how it ended. */
function Caller({ method, path }: { method: "GET" | "POST"; path: string }) {
  const [result, setResult] = useState("waiting");
  useEffect(() => {
    request(method, path).then(() => setResult("ok"), (e: ApiError) => setResult(`failed ${e.status}`));
  }, [method, path]);
  return <div>page: {result}</div>;
}

function LoginProbe() {
  const location = useLocation();
  return <div>login{location.search}</div>;
}

const at = (method: "GET" | "POST", path: string) =>
  renderAt("/w/w1/sessions?view=all", [
    { path: "/w/:ws/sessions", element: <RequireAuth><Caller method={method} path={path} /></RequireAuth> },
    { path: "/login", element: <LoginProbe /> },
  ]);

describe("a session that ends while a page is open", () => {
  it("sends the user to sign in, with the way back, when any call answers 401", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { status: 401 } });
    at("GET", "/workspaces/w1/sessions");
    expect(await screen.findByText(`login?next=${encodeURIComponent("/w/w1/sessions?view=all")}`)).toBeInTheDocument();
  });

  it("leaves the session alone when a sign-in call answers 401 (a wrong password or code is not an ended session)", async () => {
    mockApi({ "GET /me": { body: ME }, "POST /auth/mfa": { status: 401, body: { title: "invalid_credentials" } } });
    at("POST", "/auth/mfa");
    expect(await screen.findByText("page: failed 401")).toBeInTheDocument();
    expect(screen.queryByText(/^login/)).not.toBeInTheDocument();
  });

  it("leaves the session alone when a call fails for another reason", async () => {
    mockApi({ "GET /me": { body: ME }, "GET /workspaces/w1/sessions": { status: 403 } });
    at("GET", "/workspaces/w1/sessions");
    expect(await screen.findByText("page: failed 403")).toBeInTheDocument();
    expect(screen.queryByText(/^login/)).not.toBeInTheDocument();
  });
});
