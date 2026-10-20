import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { describe, expect, it, vi } from "vitest";
import { I18nProvider } from "../i18n";
import { draftOfProject } from "../lib/projectForm";
import { ME, mockApi, renderAt } from "../test/helpers";
import { project, projectMember, summary, workspaceMembers } from "../test/projects-fixtures";
import { ProjectCard } from "./ProjectCard";
import { ProjectForm } from "./ProjectForm";
import { ProjectPage } from "./ProjectPage";

function form(mirrored: boolean | undefined) {
  const onSubmit = vi.fn().mockResolvedValue(undefined);
  render(
    <I18nProvider initial="en">
      <ProjectForm initial={draftOfProject(project())} members={workspaceMembers} canChangeOwner submitLabel="Save" mirrored={mirrored} onSubmit={onSubmit} onCancel={() => {}} />
    </I18nProvider>,
  );
  return onSubmit;
}

describe("the project form of a mirrored project", () => {
  it("disables the name and the purpose and keeps every other field open", () => {
    form(true);
    expect(screen.getByLabelText("Name")).toBeDisabled();
    expect(screen.getByLabelText("Purpose")).toBeDisabled();
    expect(screen.getByLabelText("Sponsor")).toBeEnabled();
    expect(screen.getByLabelText("Owner")).toBeEnabled();
    expect(screen.getByLabelText("Start date")).toBeEnabled();
  });

  it("still submits the other fields that were changed, with the name and purpose as they were", async () => {
    const onSubmit = form(true);
    await userEvent.clear(screen.getByLabelText("Sponsor"));
    await userEvent.type(screen.getByLabelText("Sponsor"), "New board");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0]![0]).toMatchObject({ name: "Checkout", purpose: "Sell things", sponsor: "New board" });
  });

  it.each([false, undefined])("leaves the name and the purpose open when mirrored is %s", async (mirrored) => {
    const onSubmit = form(mirrored);
    expect(screen.getByLabelText("Name")).toBeEnabled();
    expect(screen.getByLabelText("Purpose")).toBeEnabled();
    await userEvent.clear(screen.getByLabelText("Name"));
    await userEvent.type(screen.getByLabelText("Name"), "Renamed");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(onSubmit.mock.calls[0]![0]).toMatchObject({ name: "Renamed" });
  });
});

describe("the project card", () => {
  const card = (over: Parameters<typeof summary>[0]) => render(
    <I18nProvider initial="en">
      <MemoryRouter><ProjectCard ws="w1" project={summary(over)} canWork onArchive={() => {}} onRestore={() => {}} /></MemoryRouter>
    </I18nProvider>,
  );

  it("shows the From Jira badge when the project came from a connection", () => {
    card({ sourceConnectionId: "c1" });
    expect(screen.getByText("From Jira")).toHaveClass("badge");
  });

  it("shows no badge for a project made in Podium", () => {
    card({ sourceConnectionId: null });
    expect(screen.queryByText("From Jira")).not.toBeInTheDocument();
  });
});

describe("the project page", () => {
  const edit = async (sourceConnectionId: string | null) => {
    mockApi({
      "GET /me": { body: ME },
      "GET /projects/p1": { body: project({ sourceConnectionId }) },
      "GET /workspaces/w1/members": { body: workspaceMembers },
      "GET /projects/p1/members": { body: [projectMember()] },
      "GET /projects/p1/sprints": { body: { items: [], next: null } },
      "GET /projects/p1/applications": { body: { items: [], next: null } },
    });
    renderAt("/w/w1/projects/p1?edit=1", [{ path: "/w/:ws/projects/:id", element: <ProjectPage /> }]);
    await screen.findByLabelText("Sponsor");
  };

  it("locks the name and the purpose of a project that came from a connection", async () => {
    await edit("c1");
    expect(screen.getByLabelText("Name")).toBeDisabled();
    expect(screen.getByLabelText("Purpose")).toBeDisabled();
  });

  it("leaves them open for a project made in Podium", async () => {
    await edit(null);
    expect(screen.getByLabelText("Name")).toBeEnabled();
    expect(screen.getByLabelText("Purpose")).toBeEnabled();
  });
});
