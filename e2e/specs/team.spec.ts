import { expect, test } from "@playwright/test";
import { mailLink, signUp } from "./helpers";

test("an owner invites a teammate who joins through the mailed link", async ({ browser }) => {
  const ownerContext = await browser.newContext();
  const owner = await ownerContext.newPage();
  await signUp(owner, "owner", "Sahip Kişi");

  const memberContext = await browser.newContext();
  const member = await memberContext.newPage();
  const memberAddress = await signUp(member, "member", "Üye Kişi");

  await owner.getByRole("link", { name: /Members/ }).click();
  // The invitations are their own tab, and the form is a drawer behind the Invite button.
  await owner.getByRole("tab", { name: /Invitations/ }).click();
  await owner.getByRole("button", { name: "Invite" }).click();
  const drawer = owner.getByRole("dialog");
  await drawer.getByLabel("E-mail to invite").fill(memberAddress);
  await drawer.getByLabel("Role").selectOption("viewer");
  await drawer.getByRole("button", { name: "Invite" }).click();
  await expect(owner.getByText("Invitation sent.")).toBeVisible();

  await member.goto(await mailLink(member.request, memberAddress));
  await expect(member.getByRole("heading", { name: "Sessions" })).toBeVisible();
  await expect(member.locator(".switcher select option")).toHaveCount(3); // own, the team's, "new workspace"

  await owner.reload();
  await expect(owner.getByText("Üye Kişi")).toBeVisible();
  await ownerContext.close();
  await memberContext.close();
});
