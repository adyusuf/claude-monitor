import { screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { SHA } from "../test/access-fixtures";
import { renderPlain } from "../test/remote-fixtures";
import { AccessLists } from "./AccessLists";

describe("the read-only access lists", () => {
  it("lists the allowed and denied folders and the applications, with the hash pinned for an application", () => {
    renderPlain(<AccessLists allowed={["/srv/app", "/srv/data"]} denied={["/srv/app/secret"]} applications={[{ path: "/usr/bin/make", sha256: SHA }, { path: "/usr/bin/cc", sha256: null }]} />);
    expect(within(screen.getByRole("list", { name: en.access.allowedFolders })).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["/srv/app", "/srv/data"]);
    expect(within(screen.getByRole("list", { name: en.access.deniedFolders })).getByText("/srv/app/secret")).toBeInTheDocument();
    const [pinned, free] = within(screen.getByRole("list", { name: en.access.applications })).getAllByRole("listitem");
    expect(pinned).toHaveTextContent(`/usr/bin/make ${en.access.sha256} ${SHA}`);
    expect(free).toHaveTextContent("/usr/bin/cc");
    expect(free).not.toHaveTextContent(en.access.sha256);
  });

  it("says each empty list means no limit, without a project folder", () => {
    renderPlain(<AccessLists allowed={[]} denied={[]} applications={[]} />);
    expect(screen.getByText(en.access.noFolderLimit)).toBeInTheDocument();
    expect(screen.getByText(en.access.noneDenied)).toBeInTheDocument();
    expect(screen.getByText(en.access.noAppLimit)).toBeInTheDocument();
    expect(screen.queryByRole("list")).toBeNull();
  });

  it("says an empty allowed list still limits to the project's own folder when the policy includes it", () => {
    renderPlain(<AccessLists allowed={[]} denied={[]} applications={[]} projectFolder />);
    expect(screen.getByText(en.access.onlyProjectFolder)).toBeInTheDocument();
    expect(screen.queryByText(en.access.noFolderLimit)).toBeNull();
  });

  it("adds that the project's own folder comes on top of the listed folders", () => {
    renderPlain(<AccessLists allowed={["/srv/app"]} denied={[]} applications={[]} projectFolder />);
    expect(screen.getByText(en.access.plusProjectFolder)).toBeInTheDocument();
  });

  it("does not mention the project's own folder for listed folders when the policy leaves it out", () => {
    renderPlain(<AccessLists allowed={["/srv/app"]} denied={[]} applications={[]} />);
    expect(screen.queryByText(en.access.plusProjectFolder)).toBeNull();
  });

  it("makes a hidden character in a path visible instead of passing it silently", () => {
    renderPlain(<AccessLists allowed={["/srv/app‮exe"]} denied={[]} applications={[]} />);
    expect(within(screen.getByRole("list", { name: en.access.allowedFolders })).getByText("\\u202E")).toHaveClass("vis-flag");
  });
});
