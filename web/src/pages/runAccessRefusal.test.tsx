import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import type { WebRunView } from "../api/types";
import { ACCESS_ERRORS } from "../api/typesAccess";
import { en } from "../i18n/en";
import { renderPlain } from "../test/remote-fixtures";
import { done } from "../test/runs-fixtures";
import { isAccessRefusal, RunAccessRefusal } from "./RunAccessRefusal";
import { RunList } from "./RunList";

const refused = (over: Partial<WebRunView>) => done("r1", 1, { status: "failed", error: "access_exe_not_allowed", ...over });
const show = (over: Partial<WebRunView>) => renderPlain(<RunAccessRefusal run={refused(over)} />);

describe("recognising an access refusal", () => {
  it.each([
    ["access_cwd_denied", true],
    ["access_something_new", true],
    ["timeout", false],
    ["output_limit", false],
    ["", false],
    [null, false],
  ] as const)("calls the error %j an access refusal: %s", (error, expected) => {
    expect(isAccessRefusal(refused({ error }))).toBe(expected);
  });
});

describe("why the target refused a run", () => {
  it.each(ACCESS_ERRORS)("says in words what %s refused", (code) => {
    show({ error: code, refusedBy: null });
    expect(screen.getByRole("alert")).toHaveTextContent(en.access.runError[code]);
  });

  it("names the project's policy and its version when the policy refused", () => {
    show({ refusedBy: "policy", accessVersion: 4 });
    expect(screen.getByText("Refused by the project's access policy (version 4).")).toBeInTheDocument();
  });

  it("names the project's policy without a version when the API sent none", () => {
    show({ refusedBy: "policy", accessVersion: null });
    expect(screen.getByText(en.access.refusedBy.policy)).toBeInTheDocument();
  });

  it("names the machine's own limit when its ceiling refused", () => {
    show({ refusedBy: "ceiling" });
    expect(screen.getByText(en.access.refusedBy.ceiling)).toBeInTheDocument();
  });

  it("names the default when the default refused", () => {
    show({ refusedBy: "default" });
    expect(screen.getByText(en.access.refusedBy.default)).toBeInTheDocument();
  });

  it("shows a layer this page does not know as it came, so a newer API is still understood", () => {
    show({ refusedBy: "quota" });
    expect(screen.getByText("quota")).toBeInTheDocument();
  });

  it("says nothing about a layer when the API named none", () => {
    show({ refusedBy: null });
    expect(screen.getByRole("alert").querySelectorAll("p")).toHaveLength(1);
  });

  it("shows a code this page does not know as it came, under the run-error label", () => {
    show({ error: "access_from_the_future", refusedBy: null });
    expect(screen.getByRole("alert")).toHaveTextContent("Message: access_from_the_future");
    expect(screen.getByText("access_from_the_future").tagName).toBe("CODE");
  });
});

describe("an access refusal in a run's details", () => {
  const list = (run: WebRunView) => renderPlain(<RunList runs={[run]} ws="w1" version={0} hasMore={false} onMore={() => undefined} onChanged={() => undefined} />);
  const openDetails = () => userEvent.click(document.querySelector<HTMLElement>(".run-toggle")!);

  it("shows the refusal and the layer in place of the plain message", async () => {
    list(refused({ refusedBy: "ceiling" }));
    await openDetails();
    expect(await screen.findByText(en.access.runError.access_exe_not_allowed)).toBeInTheDocument();
    expect(screen.getByText(en.access.refusedBy.ceiling)).toBeInTheDocument();
    expect(screen.queryByText(`${en.remote.runError}:`, { exact: false })).toBeNull();
  });

  it("shows any other error as a plain message and not as a refusal", async () => {
    list(refused({ error: "timed out waiting", refusedBy: "ceiling" }));
    await openDetails();
    expect(await screen.findByText(`${en.remote.runError}: timed out waiting`)).toBeInTheDocument();
    expect(screen.queryByText(en.access.refusedBy.ceiling)).toBeNull();
  });
});
