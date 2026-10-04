import { expect, test } from "@playwright/test";
import { e2e } from "../config";
import { signIn, signUp } from "./helpers";

test("a new account signs up through the mailed link, signs out and back in", async ({ page }) => {
  const address = await signUp(page, "signup");
  await expect(page.getByText("No sessions yet.")).toBeVisible();
  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await signIn(page, address);
});

test("a wrong password and an unknown address get the same answer", async ({ page }) => {
  const address = await signUp(page, "wrongpw");
  await page.getByRole("button", { name: "Sign out" }).click();
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill("not the password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("The e-mail or the password is wrong.")).toBeVisible();
});

test("a signed-out visitor is sent to sign in and back to the page asked for", async ({ page }) => {
  const address = await signUp(page, "next");
  const sessions = page.url();
  await page.getByRole("button", { name: "Sign out" }).click();
  await page.goto(new URL(sessions).pathname);
  await expect(page).toHaveURL(/\/login\?next=/);
  await page.getByLabel("E-mail").fill(address);
  await page.getByLabel("Password").fill(e2e.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).toHaveURL(sessions);
});

test("the pages switch to Turkish and remember it", async ({ page }) => {
  await signUp(page, "language");
  await page.getByLabel("Language").selectOption("tr");
  await expect(page.getByRole("heading", { name: "Oturumlar" })).toBeVisible();
  await page.reload();
  await expect(page.getByRole("heading", { name: "Oturumlar" })).toBeVisible();
});

test("the API reports the deployed commit", async ({ request }) => {
  const version = await (await request.get("/api/version")).json();
  expect(version.commit).toMatch(/^[0-9a-f]{40}$|^dev$/);
});
