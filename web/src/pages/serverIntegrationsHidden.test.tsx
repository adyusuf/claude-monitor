import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { en } from "../i18n/en";
import { renderAt, type Call } from "../test/helpers";
import { github, jira, serveServerSettings, serverView, smtpHidden } from "../test/server-settings-fixtures";
import { ServerIntegrationsPage } from "./ServerIntegrationsPage";

const s = en.serverSettings;
const page = () => renderAt("/server/integrations", [{ path: "/server/integrations", element: <ServerIntegrationsPage /> }]);
const mailCard = async () => within((await screen.findByRole("heading", { level: 2, name: s.names.smtp })).closest("section")!);
const saveMail = () => screen.getByRole("button", { name: s.saveNamed.replace("{name}", s.names.smtp) });
const puts = (calls: Call[]) => calls.filter((c) => c.method === "PUT");
const removeHidden = (name: string) => s.hiddenRemoveNamed.replace("{name}", name);
/** The page with an e-mail card whose server values hide saved ones (or, with false, hide nothing). */
const hiddenView = (savedHidden = true) => serverView({ integrations: [smtpHidden(savedHidden), github(), jira()] });

describe("a value saved on the page that the server's hides", () => {
  it("is named, with a remove button, under a server setting and a server secret; a server value hiding nothing has neither", async () => {
    serveServerSettings({ "GET /server/integrations": { body: hiddenView() } });
    page();
    const mail = await mailCard();
    expect(mail.getAllByText(s.hiddenSaved)).toHaveLength(2);
    for (const name of [s.fields.host, s.fields.password]) {
      const button = mail.getByRole("button", { name: removeHidden(name) });
      expect(button).toBeEnabled();
      expect(button).toHaveTextContent(s.hiddenRemove);
    }
    for (const name of [s.fields.user, s.fields.from]) expect(mail.queryByRole("button", { name: removeHidden(name) })).not.toBeInTheDocument();
    expect(mail.getByLabelText(`${s.fields.user} (${s.required})`)).toHaveAttribute("readonly");
    expect(mail.getByText(s.setOnServer.replace("{env}", "AIPOD_SMTP_PASSWORD"))).toBeInTheDocument();
    expect(saveMail()).toBeDisabled();
  });

  it("shows no hint and no button anywhere when the server says nothing is hidden", async () => {
    serveServerSettings({ "GET /server/integrations": { body: hiddenView(false) } });
    page();
    const mail = await mailCard();
    expect(mail.queryByText(s.hiddenSaved)).not.toBeInTheDocument();
    expect(mail.queryByText(s.hiddenRemove)).not.toBeInTheDocument();
  });

  it("removes the hidden secret by sending only its empty value, and Keep it takes that back", async () => {
    const calls = serveServerSettings({ "GET /server/integrations": { body: hiddenView() }, "PUT /server/integrations/smtp": { body: hiddenView() } });
    page();
    const mail = await mailCard();
    await userEvent.click(mail.getByRole("button", { name: removeHidden(s.fields.password) }));
    expect(mail.getByText(s.removing)).toBeInTheDocument();
    expect(mail.getAllByText(s.hiddenSaved)).toHaveLength(1);
    expect(mail.queryByRole("button", { name: removeHidden(s.fields.password) })).not.toBeInTheDocument();
    expect(saveMail()).toBeEnabled();
    await userEvent.click(mail.getByRole("button", { name: s.undoRemove }));
    expect(mail.queryByText(s.removing)).not.toBeInTheDocument();
    expect(mail.getAllByText(s.hiddenSaved)).toHaveLength(2);
    expect(saveMail()).toBeDisabled();
    await userEvent.click(mail.getByRole("button", { name: removeHidden(s.fields.password) }));
    await userEvent.click(saveMail());
    await waitFor(() => expect(puts(calls)).toHaveLength(1));
    expect(puts(calls)[0]!.body).toEqual({ values: { password: "" } });
    // The view that comes back still hides both: the removal mark is gone with the save.
    expect(await mail.findByText(s.savedDone)).toBeInTheDocument();
    expect(mail.getAllByText(s.hiddenSaved)).toHaveLength(2);
    expect(saveMail()).toBeDisabled();
  });

  it("rebuilds the card from the view that comes back: the removed value hides nothing any more", async () => {
    const calls = serveServerSettings({ "GET /server/integrations": { body: hiddenView() }, "PUT /server/integrations/smtp": { body: hiddenView(false) } });
    page();
    const mail = await mailCard();
    await userEvent.click(mail.getByRole("button", { name: removeHidden(s.fields.host) }));
    await userEvent.click(saveMail());
    expect(await mail.findByText(s.savedDone)).toBeInTheDocument();
    expect(puts(calls).map((c) => c.body)).toEqual([{ values: { host: "" } }]);
    expect(mail.queryByText(s.hiddenRemove)).not.toBeInTheDocument();
    expect(mail.queryByRole("button", { name: s.undoRemove })).not.toBeInTheDocument();
    expect(mail.queryByText(s.hiddenSaved)).not.toBeInTheDocument();
    expect(mail.queryByText(s.removing)).not.toBeInTheDocument();
    expect(saveMail()).toBeDisabled();
  });
});
