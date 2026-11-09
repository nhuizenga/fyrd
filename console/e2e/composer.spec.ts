import { expect, test } from "@playwright/test";
import { mockApi } from "./mockApi";
import type { TranscriptItem } from "../src/types";

const big = (n: number): TranscriptItem[] =>
  Array.from({ length: n }, (_, i) => ({
    key: `${i}:0`, seq: i, role: i % 2 ? "agent" : "user", kind: "text", at: new Date().toISOString(),
    text: `Message ${i}: **bold** and \`code\` with a [link](https://example.com)\n\n- one\n- two\n- three\n\n| a | b |\n|---|---|\n| 1 | 2 |`,
  } as TranscriptItem));

test("typing latency with a long transcript", async ({ page }) => {
  await mockApi(page, { transcripts: { session_ctl: big(300) } });
  await page.goto("/#/sessions/session_ctl");
  await expect(page.getByText("Message 299: ").first()).toBeAttached({ timeout: 30000 });
  const box = page.getByLabel("Message this session");
  await box.click();
  const ms = await page.evaluate(async () => {
    const el = document.querySelector("textarea")!;
    const times: number[] = [];
    for (let i = 0; i < 20; i++) {
      const t0 = performance.now();
      const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!;
      set.call(el, el.value + "x");
      el.dispatchEvent(new Event("input", { bubbles: true }));
      await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
      times.push(performance.now() - t0);
    }
    return times;
  });
  const avg = ms.reduce((a, b) => a + b, 0) / ms.length;
  console.log(`avg per keystroke: ${avg.toFixed(1)}ms`);
  // Generous for slow CI; before the fix this was 600ms+ at 300 items. The vitest render-count test is the exact guard.
  expect(avg).toBeLessThan(150);
});

test("a slow send clears the box at once and locks it until the reply", async ({ page }) => {
  await mockApi(page, { message: { delayMs: 1500 } });
  await page.goto("/#/sessions/session_b1");
  const box = page.getByLabel("Message this session");
  await box.fill("hello");
  const t0 = Date.now();
  await box.press("Enter");
  await expect(box).toHaveValue("");
  expect(Date.now() - t0).toBeLessThan(500);
  await expect(box).toHaveAttribute("readonly", "");
  await expect(page.getByLabel("Sending")).toContainText("hello");
  await expect(box).not.toHaveAttribute("readonly", "", { timeout: 5000 });
});

test("a failed send puts the text back and unlocks the box", async ({ page }) => {
  await mockApi(page, { message: { delayMs: 300, status: 502 } });
  await page.goto("/#/sessions/session_b1");
  const box = page.getByLabel("Message this session");
  await box.fill("do not lose me");
  await box.press("Enter");
  await expect(box).toHaveValue("do not lose me", { timeout: 5000 });
  await expect(box).not.toHaveAttribute("readonly", "");
  await expect(page.getByRole("alert")).toContainText("Upstream down");
});

test("attached files upload at once and go with the message", async ({ page }) => {
  const { sent } = await mockApi(page);
  await page.goto("/#/sessions/session_b1");
  await page.getByTestId("attach-input").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("hello") });
  await expect(page.getByRole("list", { name: "Attached files" }).getByText("notes.txt")).toBeVisible();
  await expect.poll(() => sent).toContainEqual({ url: "/api/hub/uploads", body: { name: "notes.txt" } });
  await page.getByLabel("Message this session").fill("see attached");
  await page.getByRole("button", { name: "Send" }).click();
  await expect.poll(() => sent).toContainEqual({ url: "/api/hub/sessions/session_b1/message", body: { text: "see attached", fileIds: ["file_1"] } });
  await expect(page.getByRole("list", { name: "Attached files" })).toHaveCount(0);
});

test("files can be attached to an Other answer", async ({ page }) => {
  const q = {
    id: "session_b2:req1:0", sessionId: "session_b2", sessionTitle: "builder-b2", question: "Which format?", options: ["Markdown", "HTML"],
    multiSelect: false, askedAt: new Date().toISOString(), toolUseId: "t1", requestId: "req1", input: {},
  };
  const { sent } = await mockApi(page, { questions: [q] });
  await page.goto("/#/sessions/session_b2");
  const card = page.getByRole("region", { name: "Question waiting for you" });
  await card.getByRole("button", { name: "Other" }).click();
  await card.getByLabel("Your answer").fill("see the file");
  await card.getByTestId("attach-input").setInputFiles({ name: "spec.md", mimeType: "text/markdown", buffer: Buffer.from("# spec") });
  await expect(card.getByText("spec.md")).toBeVisible();
  await card.getByRole("button", { name: "Send answer" }).click();
  await expect.poll(() => sent).toContainEqual({
    url: "/api/hub/questions/session_b2%3Areq1%3A0/answer", body: { text: "see the file", fileIds: ["file_1"] },
  });
});
