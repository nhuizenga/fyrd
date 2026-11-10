import { expect, test } from "@playwright/test";
import { SESSIONS, mockApi, session } from "./mockApi";

const S = [...SESSIONS, session({ id: "session_b5", title: "builder-zeta" })];

test("persona groups collapse with a count chip, remembered across reloads", async ({ page }) => {
  await mockApi(page, { sessions: S });
  await page.goto("/#/sessions");
  const toggle = page.locator('[data-persona="builder"] .session-group-toggle');
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  const count = await page.locator('[data-persona="builder"] li').count();
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator('[data-persona="builder"] li')).toHaveCount(0);
  await expect(toggle).toContainText("Builder");
  await expect(toggle.locator(`[aria-label="${count} sessions"]`)).toBeVisible();
  await page.reload();
  await expect(page.locator('[data-persona="builder"] .session-group-toggle')).toHaveAttribute("aria-expanded", "false");
});

test("move up saves the new order server-side and survives a reload", async ({ page }) => {
  let order = { sessions: [] as string[], groups: [] as string[] };
  const puts: unknown[] = [];
  await mockApi(page, { sessions: S });
  await page.route("**/api/session-order", async (r) => {
    if (r.request().method() === "PUT") { order = r.request().postDataJSON(); puts.push(order); }
    await r.fulfill({ json: order });
  });
  await page.goto("/#/sessions");
  const rows = page.locator('[data-persona="builder"] .session-row .session-row-text');
  await expect(rows.last()).toHaveText(/builder-zeta/);
  const before = await rows.allTextContents();
  await page.locator('[data-persona="builder"] li', { hasText: "builder-zeta" }).hover();
  await page.getByRole("button", { name: "Move builder-zeta up" }).click();
  await expect.poll(() => puts.length).toBe(1);
  await page.reload();
  const after = await rows.allTextContents();
  expect(after).not.toEqual(before);
  expect(after.indexOf("builder-zeta")).toBe(before.length - 2);
});
